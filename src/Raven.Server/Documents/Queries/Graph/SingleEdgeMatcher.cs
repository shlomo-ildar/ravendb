﻿using System;
using System.Collections.Generic;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Json;
using Raven.Server.Utils;
using Raven.Server.Config;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using static Raven.Server.Documents.Queries.GraphQueryRunner;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Documents.Queries.Graph
{
    public struct SingleEdgeMatcher
    {
        public BlittableJsonReaderObject QueryParameters;
        public List<Match> Results;
        public IGraphQueryStep Right;
        private readonly char _identityPartsSeparator;
        public Dictionary<string, BlittableJsonReaderObject> IncludedEdges;
        public WithEdgesExpression Edge;
        public StringSegment EdgeAlias;
        private readonly IJavaScriptOptions _jsOptions;
        
        public SingleEdgeMatcher(RavenConfiguration configuration, SingleEdgeMatcher step, IGraphQueryStep right, char identityPartsSeparator)
        {
            _jsOptions = DocumentDatabase.GetJsOptions(configuration);
            Right = right;
            _identityPartsSeparator = identityPartsSeparator;
            QueryParameters = step.QueryParameters;
            IncludedEdges = new Dictionary<string, BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase);
            Results = new List<Match>();
            Edge = step.Edge;
            EdgeAlias = step.EdgeAlias;
        }

        public void SingleMatch(Match left, string alias)
        {
            var dummy = left.GetSingleDocumentResult(alias);
            if (dummy == null)
                return;

            var leftDoc = dummy.Data;

            if (Edge.Where != null || Edge.Project != null)
            {
                if (BlittableJsonTraverser.Default(_jsOptions.EngineType).TryRead(leftDoc, Edge.Path.FieldValue, out var value, out _) == false || value == null)
                    return;

                var projectFieldValue = !string.IsNullOrWhiteSpace(Edge.Project?.FieldValueWithoutAlias) ?
                        Edge.Project?.FieldValueWithoutAlias : Edge.Project?.FieldValue;

                switch (value)
                {
                    case BlittableJsonReaderArray array when (Edge.Project != null || value is BlittableJsonReaderArray):
                        foreach (var item in array)
                        {
                            switch (item)
                            {
                                case BlittableJsonReaderObject json:
                                    if (projectFieldValue == null)
                                        ThrowMissingEdgeProjection();

                                    if (Edge.Where?.IsMatchedBy(_jsOptions, json, QueryParameters) != false)
                                    {
                                        AddEdgeAfterFiltering(left, json, projectFieldValue);
                                    }

                                    break;
                                case LazyCompressedStringValue compressedLazyString:
                                    HandleStringAsArrayItem(left, compressedLazyString.ToLazyStringValue());
                                    break;
                                case LazyStringValue lazyString:
                                    HandleStringAsArrayItem(left, lazyString);
                                    break;
                                default:
                                    ThrowMissingEdgeProjection();
                                    break;
                            }
                        }
                        break;
                    case BlittableJsonReaderObject json when (Edge.Project != null || value is BlittableJsonReaderArray):
                        if (projectFieldValue == null)
                            ThrowMissingEdgeProjection();

                        if (Edge.Where?.IsMatchedBy(_jsOptions, json, QueryParameters) != false)
                        {
                            AddEdgeAfterFiltering(left, json, projectFieldValue);
                        }
                        break;
                    default:
                        ThrowMissingEdgeProjection();
                        break;
                }
            }
            else
            {
                AddEdgeAfterFiltering(left, leftDoc, Edge.Path.FieldValue);
            }
        }

        private void HandleStringAsArrayItem(Match left, LazyStringValue lazyStringValue)
        {
            var jsonStringForWhere = new DynamicJsonValue
            {
                [Edge.Path.FieldValue] = lazyStringValue
            };

            if (!string.IsNullOrWhiteSpace(Edge.EdgeAlias.Value))
            {
                jsonStringForWhere.Properties.Add((Edge.EdgeAlias.Value, lazyStringValue));
            }

            using (var blittableString = lazyStringValue._context.ReadObject(jsonStringForWhere, "SingleEdgeMatcher/ReadStringAsBlittable"))
            {
                if (!(Edge.Where?.IsMatchedBy(_jsOptions, blittableString, QueryParameters) ?? true))
                    return;
            }

            var jsonStringForProjection = new DynamicJsonValue
            {
                [Edge.Path.FieldValue] = lazyStringValue
            };

            var edgeJsonString = lazyStringValue._context.ReadObject(jsonStringForProjection, "SingleEdgeMatcher/ReadStringAsBlittable");
            AddEdgeAfterFiltering(left, edgeJsonString, Edge.Path.FieldValue);
        }

        private void ThrowMissingEdgeProjection()
        {
            throw new InvalidQueryException("An expression that selects an edge must have a projection with exactly one field which is of type string.", Edge.ToString());
        }


        private static bool ShouldUseFullObjectForEdge(BlittableJsonReaderObject src, BlittableJsonReaderObject json)
        {
            return json != null && (json != src || src.HasParent);
        }


        private void AddEdgeAfterFiltering(Match left, BlittableJsonReaderObject leftDoc, StringSegment path)
        {
            var edgeIncludeOp = new EdgeIncludeOp(IncludedEdges);
            IncludedEdges.Clear();
            IncludeUtil.GetDocIdFromInclude(null, leftDoc,
                 path,
                 _identityPartsSeparator,
                 edgeIncludeOp);

            if (IncludedEdges.Count == 0)
                return;

            foreach (var includedEdge in IncludedEdges)
            {
                if (Right == null)
                {
                    MergeAndAddResult(left, leftDoc, includedEdge.Key, includedEdge.Value, default);
                    continue;
                }
                foreach (var rightMatch in Right.GetById(includedEdge.Key))
                {
                    MergeAndAddResult(left, leftDoc, includedEdge.Key, includedEdge.Value, rightMatch);
                }
            }
        }

        private void MergeAndAddResult(Match left, BlittableJsonReaderObject leftDoc, string includeKey, BlittableJsonReaderObject includedValue, Match rightMatch)
        {
            var clone = new Match(left);
            clone.Merge(rightMatch);

            if (ShouldUseFullObjectForEdge(leftDoc, includedValue))
                clone.Set(EdgeAlias, includedValue);
            else
                clone.Set(EdgeAlias, includeKey);

            Results.Add(clone);
        }

        private struct EdgeIncludeOp : IncludeUtil.IIncludeOp
        {
            private readonly Dictionary<string, BlittableJsonReaderObject> _edges;

            public EdgeIncludeOp(Dictionary<string, BlittableJsonReaderObject> edges)
            {
                _edges = edges;
            }


            public void Include(BlittableJsonReaderObject parent, string id)
            {
                if (id == null)
                    return;

                _edges[id] = parent;
            }
        }
    }
}
