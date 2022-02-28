﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Json;
using Raven.Server.ServerWide;
using Raven.Server.Config;
using Sparrow;
using Sparrow.Json;
using static Raven.Server.Documents.Queries.GraphQueryRunner;

namespace Raven.Server.Documents.Queries.Graph
{
    public class EdgeQueryStep : IGraphQueryStep
    {
        private RavenConfiguration _configuration;
        private List<Match> _results = new List<Match>();
        private int _index = -1;

        private string _outputAlias;

        private HashSet<string> _aliases;

        public IGraphQueryStep Left => _left;
        public IGraphQueryStep Right => _right;

        public EdgeQueryStep(RavenConfiguration configuration, IGraphQueryStep left, IGraphQueryStep right, WithEdgesExpression edgesExpression, MatchPath edgePath, BlittableJsonReaderObject queryParameters, OperationCancelToken token)
        {
            _configuration = configuration;
            _left = left;
            _right = right;

            _aliases = new HashSet<string>();

            _aliases.UnionWith(_left.GetAllAliases());
            _aliases.UnionWith(_right.GetAllAliases());
            _aliases.Add(edgePath.Alias.Value);

            _edgePath = edgePath;
            _queryParameters = queryParameters;
            _edgesExpression = edgesExpression;

            _outputAlias = _right.GetOutputAlias();
            _token = token;
        }

        public EdgeQueryStep(IGraphQueryStep left, IGraphQueryStep right, EdgeQueryStep eqs, OperationCancelToken token)
        {
            _left = left;
            _right = right;
            _aliases = new HashSet<string>();

            _aliases.UnionWith(_left.GetAllAliases());
            _aliases.UnionWith(_right.GetAllAliases());
            _aliases.Add(eqs._edgePath.Alias.Value);

            _edgePath = eqs._edgePath;
            _queryParameters = eqs._queryParameters;
            _edgesExpression = eqs._edgesExpression;


            _outputAlias = _right.GetOutputAlias();
            _token = token;
        }

        public bool IsEmpty()
        {
            return _results.Count == 0;
        }

        public bool CollectIntermediateResults { get; set; }

        public List<Match> IntermediateResults => CollectIntermediateResults?_results: new List<GraphQueryRunner.Match>();

        public IGraphQueryStep Clone()
        {
            return new EdgeQueryStep(_configuration, _left.Clone(), _right.Clone(), _edgesExpression, _edgePath, _queryParameters, _token)
            {
                CollectIntermediateResults = CollectIntermediateResults
            };
        }

        public ValueTask Initialize()
        {
            if (_index != -1)
                return default;

            _token.ThrowIfCancellationRequested();
            var leftTask = _left.Initialize();
            if (leftTask.IsCompleted)
            {
                //At this point we know we are not going to yield results we can skip running right hand side
                if (Left.IsEmpty())
                {
                    _index = 0;
                    return default;
                }

                _token.ThrowIfCancellationRequested();
                var rightTask = _right.Initialize();
                if (rightTask.IsCompleted)
                {
                    CompleteInitialization();
                    return default;
                }
                return new ValueTask(InitializeRightAsync(rightTask));
            }

            return new ValueTask(InitializeLeftAsync(leftTask));
        }

        private async Task InitializeRightAsync(ValueTask rightTask)
        {
            _token.ThrowIfCancellationRequested();
            await rightTask;
            CompleteInitialization();
        }

        private async Task InitializeLeftAsync(ValueTask leftTask)
        {
            _token.ThrowIfCancellationRequested();
            await leftTask;
            //At this point we know we are not going to yield results we can skip running right hand side
            if (Left.IsEmpty())
            {
                _index = 0;
                return;
            }
            var rightTask = _right.Initialize();
            if (rightTask.IsCompleted == false)
            {
                await rightTask;
            }
            CompleteInitialization();
        }

        public ISingleGraphStep GetSingleGraphStepExecution()
        {
            return new EdgeMatcher(this);
        }

        private void CompleteInitialization()
        {            
            _index = 0;

            if (_right.IsEmpty())
                return;

            var edgeMatcher = new EdgeMatcher(this);
            var alias = _left.GetOutputAlias();
            while (_left.GetNext(out var left))
            {
                _token.ThrowIfCancellationRequested();
                edgeMatcher.Run(left, alias);
            }
        }

        public class EdgeMatcher : ISingleGraphStep
        {
            internal readonly EdgeQueryStep _parent;
            SingleEdgeMatcher _processor;

            public EdgeMatcher(EdgeQueryStep parent)
            {
                var edgeAlias = parent._edgePath.Alias;
                var edge = parent._edgesExpression;
                edge.EdgeAlias = edgeAlias;

                _processor = new SingleEdgeMatcher
                {
                    IncludedEdges = new Dictionary<string, BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase),

                    QueryParameters = parent._queryParameters,
                    Results = parent._results,
                    Right = parent._right,
                    Edge = edge,
                    EdgeAlias = edgeAlias
                };
                _parent = parent;
            }

            public bool GetAndClearResults(List<Match> matches)
            {
                if (_processor.Results.Count == 0)
                    return false;

                matches.AddRange(_processor.Results);
                _processor.Results.Clear();

                return true;
            }


            public void AddAliases(HashSet<string> aliases)
            {
                aliases.UnionWith(_parent.GetAllAliases());
            }

            public void SetPrev(IGraphQueryStep prev)
            {
                _parent._left = prev;
            }

            public ValueTask Initialize()
            {
                if (_parent._left != null)
                {
                    var leftTask = _parent._left.GetSingleGraphStepExecution().Initialize();
                    if (leftTask.IsCompleted == false)
                    {
                        return CompleteRightInitAsync(leftTask);
                    }
                }
                return _parent._right.GetSingleGraphStepExecution().Initialize();
            }

            private async ValueTask CompleteRightInitAsync(ValueTask leftTask)
            {
                await leftTask;
                await _parent._right.Initialize();
            }

            public void Run(Match src, string alias)
            {
                _processor.SingleMatch(src, alias);
            }
        }


        public bool GetNext(out Match match)
        {
            _token.ThrowIfCancellationRequested();
            if (_index >= _results.Count)
            {
                match = default;
                return false;
            }

            match = _results[_index++];
            return true;
        }


        public string GetOuputAlias()
        {
            return _right.GetOutputAlias();
        }

        public HashSet<string> GetAllAliases()
        {
            return _aliases;
        }

        public void Analyze(Match match, GraphQueryRunner.GraphDebugInfo graphDebugInfo)
        {
            _left.Analyze(match, graphDebugInfo);
            _right.Analyze(match, graphDebugInfo);

            var prev = match.GetResult(_left.GetOutputAlias());

            AnalyzeEdge(_configuration, _edgesExpression, _edgePath.Alias, match, prev, graphDebugInfo);
        }

        public static string AnalyzeEdge(RavenConfiguration configuration, WithEdgesExpression _edgesExpression, StringSegment edgeAlias, Match match, object prev,
            GraphDebugInfo graphDebugInfo)
        {
            var edge = match.GetResult(edgeAlias.Value);

            if (edge == null || prev == null)
                return null;

            string result = null;

            if (edge is string s)
            {
                result = s;
            }
            else if (edge is LazyStringValue lsv)
            {
                result = lsv.ToString();
            }
            else if (edge is BlittableJsonReaderObject bjro)
            {
                if (_edgesExpression.Project != null)
                {
                    if (BlittableJsonTraverser.Default.TryRead(bjro, _edgesExpression.Project.FieldValue, out var id, out _))
                    {
                        if (id != null)
                        {
                            result = id.ToString();
                        }
                    }
                }
            }

            if (result == null)
                return null;

            string source = null;
            if (prev is string str)
            {
                source = str;
            }
            else if (prev is Document d)
            {
                source = d.Id;
            }

            graphDebugInfo.AddEdge(edgeAlias.Value, edge, source, result);

            return result;
        }

        public List<Match> GetById(string id)
        {
            throw new NotSupportedException("Cannot get a match by id from an edge");
        }

        public string GetOutputAlias()
        {
            return _outputAlias;
        }

        private IGraphQueryStep _left;
        private IGraphQueryStep _right;
        private MatchPath _edgePath;
        private readonly BlittableJsonReaderObject _queryParameters;
        private WithEdgesExpression _edgesExpression;
        private OperationCancelToken _token;

        public void SetRight(IGraphQueryStep newRight)
        {
            _right = newRight;
        }
    }
}
