﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Dynamic;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Sparrow.Server;
using static Raven.Server.Documents.Queries.GraphQueryRunner;
using Index = Raven.Server.Documents.Indexes.Index;

namespace Raven.Server.Documents.Queries.Graph
{
    public class GraphQueryPlan
    {
        public IGraphQueryStep RootQueryStep;
        private readonly IndexQueryServerSide _query;
        private readonly QueryOperationContext _context;
        private readonly long? _resultEtag;
        private readonly OperationCancelToken _token;
        private readonly DocumentDatabase _database;
        public bool IsStale { get; set; }
        public long ResultEtag { get; set; }
        public GraphQuery GraphQuery => _query.Metadata.Query.GraphQuery;
        public bool CollectIntermediateResults { get; set; }
        public Dictionary<string, DocumentQueryResult> QueryCache = new Dictionary<string, DocumentQueryResult>();
        public Dictionary<string, Reference<int>> IdenticalQueriesCount = new Dictionary<string, Reference<int>>();

        private readonly DynamicQueryRunner _dynamicQueryRunner;

        public GraphQueryPlan(IndexQueryServerSide query, QueryOperationContext context, long? resultEtag,
            OperationCancelToken token, DocumentDatabase database)
        {
            _database = database;
            _query = query;
            _context = context;
            _resultEtag = resultEtag;
            _token = token;
            _dynamicQueryRunner = new DynamicQueryRunner(database);
        }

        public void BuildQueryPlan()
        {
            new GraphQuerySyntaxValidatorVisitor(GraphQuery).Visit(_query.Metadata.Query); //this will throw if the syntax will be bad
            RootQueryStep = BuildQueryPlanForExpression(_query.Metadata.Query.GraphQuery.MatchClause);
            ClearUniqueQueriesFromIdenticalQueries();
        }

        private void ClearUniqueQueriesFromIdenticalQueries()
        {
            var tmp = new Dictionary<string, Reference<int>>();
            foreach (var keyVal in IdenticalQueriesCount)
            {
                if (keyVal.Value.Value > 1)
                {
                    tmp.Add(keyVal.Key, keyVal.Value);
                }
            }
            IdenticalQueriesCount = tmp;
        }

        private IGraphQueryStep BuildQueryPlanForExpression(QueryExpression expression)
        {
            switch (expression)
            {
                case PatternMatchElementExpression pme:
                    return BuildQueryPlanForPattern(pme, 0);
                case BinaryExpression be:
                    return BuildQueryPlanForBinaryExpression(be);
                default:
                    throw new ArgumentOutOfRangeException($"Unexpected expression of type {expression.Type}");
            }
        }

        public void Analyze(List<Match> matches, GraphDebugInfo graphDebugInfo)
        {
            foreach (var match in matches)
            {
                RootQueryStep.Analyze(match, graphDebugInfo);
            }
        }

        public async Task Initialize()
        {
            await RootQueryStep.Initialize();
        }

        private IGraphQueryStep BuildQueryPlanForBinaryExpression(BinaryExpression be)
        {
            bool negated = false;
            var rightExpr = be.Right;
            if (be.Right is NegatedExpression n)
            {
                negated = true;
                rightExpr = n.Expression;
            }

            var left = BuildQueryPlanForExpression(be.Left);
            var right = BuildQueryPlanForExpression(rightExpr);
            switch (be.Operator)
            {
                case OperatorType.And:
                    if (negated)
                        return new IntersectionQueryStep<Except>(left, right, _token)
                        {
                            CollectIntermediateResults = CollectIntermediateResults
                        };
                    return new IntersectionQueryStep<Intersection>(left, right, _token, returnEmptyIfRightEmpty: true)
                    {
                        CollectIntermediateResults = CollectIntermediateResults
                    };

                case OperatorType.Or:
                    return new IntersectionQueryStep<Union>(left, right, _token, returnEmptyIfLeftEmpty: false)
                    {
                        CollectIntermediateResults = CollectIntermediateResults
                    };

                default:
                    throw new ArgumentOutOfRangeException($"Unexpected binary expression of type: {be.Operator}");
            }
        }

        private IGraphQueryStep BuildQueryPlanForPattern(PatternMatchElementExpression patternExpression, int start)
        {
            var prev = BuildQueryPlanForMatchNode(patternExpression.Path[start]);

            for (int i = start + 1; i < patternExpression.Path.Length; i += 2)
            {
                if (patternExpression.Path[i].Recursive == null)
                {
                    var next = i + 1 < patternExpression.Path.Length ?
                      BuildQueryPlanForMatchNode(patternExpression.Path[i + 1]) :
                      null;
                    prev = BuildQueryPlanForEdge(prev, next, patternExpression.Path[i]);
                }
                else
                {
                    return BuildQueryPlanForRecursiveEdge(prev, i, patternExpression);
                }
            }

            return prev;
        }

        private IGraphQueryStep BuildQueryPlanForEdge(IGraphQueryStep left, IGraphQueryStep right, MatchPath edge)
        {
            var alias = edge.Alias;

            if (GraphQuery.WithEdgePredicates.TryGetValue(alias, out var withEdge) == false)
            {
                throw new InvalidOperationException($"BuildQueryPlanForEdge was invoked for alias='{alias}' which suppose to be an edge but no corresponding WITH EDGE clause was found.");
            }

            return new EdgeQueryStep(_database.Configuration, left, right, withEdge, edge, _query.QueryParameters, _token)
            {
                CollectIntermediateResults = CollectIntermediateResults
            };
        }

        private IGraphQueryStep BuildQueryPlanForRecursiveEdge(IGraphQueryStep left, int index, PatternMatchElementExpression patternExpression)
        {
            var recursive = patternExpression.Path[index].Recursive.Value;
            var pattern = recursive.Pattern;
            var steps = new List<SingleEdgeMatcher>((pattern.Count + 1) / 2);
            for (int i = 0; i < pattern.Count; i += 2)
            {
                if (GraphQuery.WithEdgePredicates.TryGetValue(pattern[i].Alias, out var recursiveEdge) == false)
                {
                    throw new InvalidOperationException($"BuildQueryPlanForEdge was invoked for recursive alias='{pattern[i].Alias}' which suppose to be an edge but no corresponding WITH EDGE clause was found.");
                }

                steps.Add(new SingleEdgeMatcher
                {
                    IncludedEdges = new Dictionary<string, Sparrow.Json.BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase),
                    QueryParameters = _query.QueryParameters,
                    Edge = recursiveEdge,
                    Results = new List<Match>(),
                    Right = i + 1 < pattern.Count ? BuildQueryPlanForMatchNode(pattern[i + 1]) : null,
                    EdgeAlias = pattern[i].Alias
                });
            }

            var recursiveStep = new RecursionQueryStep(_database.Configuration, left, steps, recursive, recursive.GetOptions(_query.Metadata, _query.QueryParameters), _token)
            {
                CollectIntermediateResults = CollectIntermediateResults
            };

            if (index + 1 < patternExpression.Path.Length)
            {
                if (patternExpression.Path[index + 1].Recursive.HasValue)
                {
                    throw new InvalidQueryException("Two adjacent 'recursive' queries are not allowed", GraphQuery.QueryText);
                }

                if (patternExpression.Path[index + 1].IsEdge)
                {
                    var nextPlan = BuildQueryPlanForPattern(patternExpression, index + 2);
                    nextPlan = BuildQueryPlanForEdge(recursiveStep, nextPlan, patternExpression.Path[index + 1]);
                    recursiveStep.SetNext(nextPlan.GetSingleGraphStepExecution());
                }
                else
                {
                    var nextPlan = BuildQueryPlanForPattern(patternExpression, index + 1);
                    recursiveStep.SetNext(nextPlan.GetSingleGraphStepExecution());
                }
            }


            return recursiveStep;
        }

        private IGraphQueryStep BuildQueryPlanForMatchNode(MatchPath node)
        {
            var alias = node.Alias;
            if (GraphQuery.WithDocumentQueries.TryGetValue(alias, out var query) == false)
            {
                throw new InvalidOperationException($"BuildQueryPlanForMatchVertex was invoked for allias='{alias}' which is supposed to be a node but no corresponding WITH clause was found.");
            }
            // TODO: we can tell at this point if it is a collection query or not,
            // TODO: in the future, we want to build a diffrent step for collection queries in the future.        
            var queryMetadata = new QueryMetadata(query.withQuery, _query.QueryParameters, 0, addSpatialProperties: false);
            var qqs = new QueryQueryStep(_database.QueryRunner, alias, query.withQuery, queryMetadata, _query.QueryParameters, _context, _resultEtag, this, _token)
            {
                CollectIntermediateResults = CollectIntermediateResults
            };
            var key = qqs.GetQueryString;
            //We only want to cache queries that are not unique so we count them during their creation
            if (IdenticalQueriesCount.TryGetValue(key, out var count))
            {
                count.Value += 1;
            }
            else
            {
                IdenticalQueriesCount.Add(key, new Reference<int>() { Value = 1 });
            }

            return qqs;
        }

        public void OptimizeQueryPlan()
        {
            var cdqsr = new EdgeCollectionDestinationRewriter(_database.DocumentsStorage, _token);
            RootQueryStep = cdqsr.Visit(RootQueryStep);
        }

        public List<Match> Execute()
        {
            var list = new List<Match>();
            while (RootQueryStep.GetNext(out var m))
            {
                list.Add(m);
            }
            return list;
        }

        public async Task CreateAutoIndexesAndWaitIfNecessary()
        {
            var queryStepsGatherer = new QueryQueryStepGatherer();
            await queryStepsGatherer.VisitAsync(RootQueryStep);

            if (_context.AreTransactionsOpened() == false)
                _context.OpenReadTransaction();

            try
            {
                var etag = DocumentsStorage.ReadLastEtag(_context.Documents.Transaction.InnerTransaction);
                var queryDuration = Stopwatch.StartNew();
                var indexes = new List<Index>();
                var indexWaiters = new Dictionary<Index, (IndexQueryServerSide, AsyncWaitForIndexing)>();
                foreach (var queryStepInfo in queryStepsGatherer.QuerySteps)
                {
                    if (string.IsNullOrWhiteSpace(queryStepInfo.QueryStep.Query.From.From.FieldValue) || queryStepInfo.IsIndexQuery)
                        continue;
                    var indexQuery = new IndexQueryServerSide(queryStepInfo.QueryStep.GetQueryString, queryStepInfo.QueryStep.QueryParameters);
                    //No sense creating an index for collection queries
                    if (indexQuery.Metadata.IsCollectionQuery)
                        continue;
                    var indexCreationInfo = await _dynamicQueryRunner.CreateAutoIndexIfNeeded(indexQuery, true, null, _database.DatabaseShutdown);
                    if (indexCreationInfo.HasCreatedAutoIndex) //wait for non-stale only IF we just created an auto-index
                    {
                        indexes.Add(indexCreationInfo.Index);
                        var queryTimeout = indexQuery.WaitForNonStaleResultsTimeout ?? Index.DefaultWaitForNonStaleResultsTimeout;
                        indexWaiters.Add(indexCreationInfo.Index, (indexQuery, new AsyncWaitForIndexing(queryDuration, queryTimeout, indexCreationInfo.Index)));
                    }
                }

                await WaitForNonStaleResultsInternal(etag, indexes, indexWaiters);
            }
            finally
            {
                //The rest of the code assumes that a Tx is not opened
                _context.CloseTransaction();
            }
        }

        public async Task<bool> WaitForNonStaleResults()
        {
            if (_context.AreTransactionsOpened() == false)
                _context.OpenReadTransaction();

            var etag = DocumentsStorage.ReadLastEtag(_context.Documents.Transaction.InnerTransaction);
            var queryDuration = Stopwatch.StartNew();
            var indexNamesGatherer = new GraphQueryIndexNamesGatherer();
            await indexNamesGatherer.VisitAsync(RootQueryStep);
            var indexes = new List<Index>();
            var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexWaiters = new Dictionary<Index, (IndexQueryServerSide, AsyncWaitForIndexing)>();
            var queryTimeout = _query.WaitForNonStaleResultsTimeout ?? Index.DefaultWaitForNonStaleResultsTimeout;
            foreach (var indexName in indexNamesGatherer.Indexes)
            {
                if (indexNames.Add(indexName) == false)
                    continue;
                var index = _database.IndexStore.GetIndex(indexName);
                indexes.Add(index);
                indexWaiters.Add(index, (_query, new AsyncWaitForIndexing(queryDuration, queryTimeout, index)));
            }

            foreach (var qqs in indexNamesGatherer.QueryStepsWithoutExplicitIndex)
            {
                // we need to close the transaction since the DynamicQueryRunner.MatchIndex
                // expects that the transaction will be closed
                _context.CloseTransaction();

                //this will ensure that query step has relevant index
                //if needed, this will create auto-index                
                var query = new IndexQueryServerSide(qqs.Query.ToString(), qqs.QueryParameters);
                var index = await _dynamicQueryRunner.MatchIndex(query, true, null, _database.DatabaseShutdown);
                if (indexNames.Add(index.Name) == false)
                    continue;
                indexes.Add(index);
                indexWaiters.Add(index, (_query, new AsyncWaitForIndexing(queryDuration, queryTimeout, index)));
            }

            return await WaitForNonStaleResultsInternal(etag, indexes, indexWaiters);
        }

        private async Task<bool> WaitForNonStaleResultsInternal(long etag, List<Index> indexes, Dictionary<Index, (IndexQueryServerSide Query, AsyncWaitForIndexing Waiter)> indexWaiters)
        {
            var staleIndexes = indexes;
            var frozenAwaiters = new Dictionary<Index, AsyncManualResetEvent.FrozenAwaiter>();

            while (true)
            {
                //If we found a stale index we already disposed of the old transaction
                if (_context.AreTransactionsOpened() == false)
                    _context.OpenReadTransaction();

                //see https://issues.hibernatingrhinos.com/issue/RavenDB-5576
                frozenAwaiters.Clear();
                foreach (var index in staleIndexes)
                {
                    frozenAwaiters.Add(index, index.GetIndexingBatchAwaiter());
                }

                staleIndexes = GetStaleIndexes(staleIndexes, etag);
                //All indexes are not stale we can continue without waiting
                if (staleIndexes.Count == 0)
                {
                    //false means we are not stale
                    return false;
                }

                bool foundStaleIndex = false;
                bool indexTimedout = false;
                //here we will just wait for the first stale index we find
                foreach (var index in staleIndexes)
                {
                    var indexAwaiter = indexWaiters[index].Waiter;
                    var query = indexWaiters[index].Query;

                    //if any index timedout we are stale
                    indexTimedout |= indexAwaiter.TimeoutExceeded;

                    if (Index.WillResultBeAcceptable(true, query, indexWaiters[index].Waiter) == false)
                    {
                        _context.CloseTransaction();
                        await indexAwaiter.WaitForIndexingAsync(frozenAwaiters[index]).ConfigureAwait(false);
                        foundStaleIndex = true;
                        break;
                    }
                }

                //We are done waiting for all stale indexes
                if (foundStaleIndex == false)
                {
                    //we might get here if all indexes have timedout 
                    return indexTimedout;
                }
            }
        }

        private List<Index> GetStaleIndexes(List<Index> indexes, long etag)
        {
            var staleList = new List<Index>();
            foreach (var index in indexes)
            {
                if (index.IsStale(_context, etag))
                {
                    staleList.Add(index);
                }
            }

            return staleList;
        }

    }
}
