﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Database.Indexing;

namespace Raven.Database.Queries
{
	public class FacetedQueryRunner
	{
		private readonly DocumentDatabase database;

		public FacetedQueryRunner(DocumentDatabase database)
		{
			this.database = database;
		}

		public FacetResults GetFacets(string index, IndexQuery indexQuery, List<Facet> facets, int start = 0, int? pageSize = null)
		{
			var results = new FacetResults();
			var defaultFacets = new Dictionary<string, Facet>();
			var rangeFacets = new Dictionary<string, List<ParsedRange>>();

			foreach (var facet in facets)
			{
                defaultFacets[facet.Name] = facet;
                if (facet.Aggregation != FacetAggregation.Count)
                {
                    if (string.IsNullOrEmpty(facet.AggregationField))
                        throw new InvalidOperationException("Facet " + facet.Name + " cannot have aggregation set to " +
                                                            facet.Aggregation + " without having a value in AggregationField");

                    if (facet.AggregationField.EndsWith("_Range") == false)
                        facet.AggregationField = facet.AggregationField + "_Range";
                }
                switch (facet.Mode)
				{
					case FacetMode.Default:
						results.Results[facet.Name] = new FacetResult();
				        break;
					case FacetMode.Ranges:
						rangeFacets[facet.Name] = facet.Ranges.Select(range => ParseRange(facet.Name, range)).ToList();
						results.Results[facet.Name] = new FacetResult
						{
							Values = facet.Ranges.Select(range => new FacetValue
							{
								Range = range,
							}).ToList()
						};

						break;
					default:
						throw new ArgumentException(string.Format("Could not understand '{0}'", facet.Mode));
				}
			}

			var queryForFacets = new QueryForFacets(database, index, defaultFacets, rangeFacets, indexQuery, results, start, pageSize);
			queryForFacets.Execute();

			return results;
		}

		private static ParsedRange ParseRange(string field, string range)
		{
			var parts = range.Split(new[] { " TO " }, 2, StringSplitOptions.RemoveEmptyEntries);

			if (parts.Length != 2)
				throw new ArgumentException("Could not understand range query: " + range);

			var trimmedLow = parts[0].Trim();
			var trimmedHigh = parts[1].Trim();
			var parsedRange = new ParsedRange
			{
				Field = field,
				RangeText = range,
				LowInclusive = IsInclusive(trimmedLow.First()),
				HighInclusive = IsInclusive(trimmedHigh.Last()),
				LowValue = trimmedLow.Substring(1),
				HighValue = trimmedHigh.Substring(0, trimmedHigh.Length - 1)
			};

			if (RangeQueryParser.NumericRangeValue.IsMatch(parsedRange.LowValue))
			{
				parsedRange.LowValue = NumericStringToSortableNumeric(parsedRange.LowValue);
			}

			if (RangeQueryParser.NumericRangeValue.IsMatch(parsedRange.HighValue))
			{
				parsedRange.HighValue = NumericStringToSortableNumeric(parsedRange.HighValue);
			}


			if (parsedRange.LowValue == "NULL" || parsedRange.LowValue == "*")
				parsedRange.LowValue = null;
			if (parsedRange.HighValue == "NULL" || parsedRange.HighValue == "*")
				parsedRange.HighValue = null;



			return parsedRange;
		}

		private static string NumericStringToSortableNumeric(string value)
		{
			var number = NumberUtil.StringToNumber(value);
			if (number is int)
			{
				return NumericUtils.IntToPrefixCoded((int)number);
			}
			if (number is long)
			{
				return NumericUtils.LongToPrefixCoded((long)number);
			}
			if (number is float)
			{
				return NumericUtils.FloatToPrefixCoded((float)number);
			}
			if (number is double)
			{
				return NumericUtils.DoubleToPrefixCoded((double)number);
			}

			throw new ArgumentException("Unknown type for " + number.GetType() + " which started as " + value);
		}

		private static bool IsInclusive(char ch)
		{
			switch (ch)
			{
				case '[':
				case ']':
                    return false;
				case '{':
				case '}':
			        return true;
				default:
					throw new ArgumentException("Could not understand range prefix: " + ch);
			}
		}

		private class ParsedRange
		{
			public bool LowInclusive;
			public bool HighInclusive;
			public string LowValue;
			public string HighValue;
			public string RangeText;
			public string Field;

			public bool IsMatch(string value)
			{
				var compareLow =
					LowValue == null
						? -1
						: string.CompareOrdinal(value, LowValue);
				var compareHigh = HighValue == null ? 1 : string.CompareOrdinal(value, HighValue);
				// if we are range exclusive on either end, check that we will skip the edge values
				if (compareLow == 0 && LowInclusive == false ||
					compareHigh == 0 && HighInclusive == false)
					return false;

				if (LowValue != null && compareLow < 0)
					return false;

				if (HighValue != null && compareHigh > 0)
					return false;

				return true;
			}

			public override string ToString()
			{
				return string.Format("{0}:{1}", Field, RangeText);
			}
		}

		private class QueryForFacets
		{
            Dictionary<FacetValue, HashSet<int>> matches = new Dictionary<FacetValue, HashSet<int>>();
		    private IndexDefinition indexDefinition;

		    public QueryForFacets(
				DocumentDatabase database,
				string index,
				 Dictionary<string, Facet> facets,
				 Dictionary<string, List<ParsedRange>> ranges,
				 IndexQuery indexQuery,
				 FacetResults results,
				 int start,
				 int? pageSize)
			{
				Database = database;
				Index = index;
				Facets = facets;
				Ranges = ranges;
				IndexQuery = indexQuery;
				Results = results;
				Start = start;
				PageSize = pageSize;
		        indexDefinition = Database.IndexDefinitionStorage.GetIndexDefinition(this.Index);
			}

			DocumentDatabase Database { get; set; }
			string Index { get; set; }
			Dictionary<string, Facet> Facets { get; set; }
			Dictionary<string, List<ParsedRange>> Ranges { get; set; }
			IndexQuery IndexQuery { get; set; }
			FacetResults Results { get; set; }
			private int Start { get; set; }
			private int? PageSize { get; set; }

			public void Execute()
			{
				//We only want to run the base query once, so we capture all of the facet-ing terms then run the query
				//	once through the collector and pull out all of the terms in one shot
				var allCollector = new GatherAllCollector();
                var facetsByName = new Dictionary<string, Dictionary<string, FacetValue>>();

				IndexSearcher currentIndexSearcher;
				using (Database.IndexStorage.GetCurrentIndexSearcher(Index, out currentIndexSearcher))
				{
					var baseQuery = Database.IndexStorage.GetLuceneQuery(Index, IndexQuery, Database.IndexQueryTriggers);
					currentIndexSearcher.Search(baseQuery, allCollector);
					var fields = Facets.Values.Select(x => x.Name)
							.Concat(Ranges.Select(x => x.Key));
					var fieldsToRead = new HashSet<string>(fields);
					IndexedTerms.ReadEntriesForFields(currentIndexSearcher.IndexReader,
						fieldsToRead,
						allCollector.Documents,
						(term, doc) =>
						{
                            Facet value;
						    if (Facets.TryGetValue(term.Field, out value) == false)
						        return;

						    switch (value.Mode)
						    {
						        case FacetMode.Default:
                                    var facetValues = facetsByName.GetOrAdd(term.Field);
						            FacetValue existing;
						            if (facetValues.TryGetValue(term.Text, out existing) == false)
						            {
						                existing = new FacetValue{Range = term.Text};
						                facetValues[term.Text] = existing;
						            }
						            UpdateValue(existing, value, doc);
						            break;
						        case FacetMode.Ranges:
                                    List<ParsedRange> list;
							        if (Ranges.TryGetValue(term.Field, out list))
							        {
								        for (int i = 0; i < list.Count; i++)
								        {
									        var parsedRange = list[i];
									        if (parsedRange.IsMatch(term.Text)) 
									        {
									            var facetValue = Results.Results[term.Field].Values[i];
                                                UpdateValue(facetValue, value, doc);
									        }
								        }
							        }
						            break;
						        default:
						            throw new ArgumentOutOfRangeException();
						    }
						});
                    UpdateFacetResults(facetsByName);

                    foreach (var result in Results.Results)
                    {
                        CompleteSingleFacetCalc(result.Value.Values, Facets[result.Key], currentIndexSearcher.IndexReader);
                    }
				}
			}

		    private void CompleteSingleFacetCalc(IEnumerable<FacetValue> valueCollection, Facet facet, IndexReader indexReader)
		    {
		        var fieldsToRead = new HashSet<string> {facet.AggregationField};
                foreach (var facetValue in valueCollection)
		        {
		            HashSet<int> set;
		            if(matches.TryGetValue(facetValue, out set) == false)
                        continue;
		            var val = GetDefaultValue(facet);
		            IndexedTerms.ReadEntriesForFields(indexReader, fieldsToRead, set, (term, i) =>
		            {
		                var currentVal = GetValueFromIndex(facet, term);
		                switch (facet.Aggregation)
		                {
		                    case FacetAggregation.Count:
		                        val++;
		                        break;
		                    case FacetAggregation.Max:
		                        val = Math.Max(val, currentVal);
		                        break;
		                    case FacetAggregation.Min:
                                val = Math.Min(val, currentVal);
		                        break;
		                    case FacetAggregation.Average:
		                    case FacetAggregation.Sum:
		                        val += currentVal;
		                        break;
		                    default:
		                        throw new ArgumentOutOfRangeException();
		                }
		            });
					
		            switch (facet.Aggregation)
		            {
		                case FacetAggregation.Average:
		                    if (facetValue.Hits != 0)
		                        facetValue.Value = val/facetValue.Hits;
		                    else
		                        facetValue.Value = double.NaN;
		                    break;
		                    //nothing to do here
		                case FacetAggregation.Count:
		                case FacetAggregation.Max:
		                case FacetAggregation.Min:
		                case FacetAggregation.Sum:
		                    facetValue.Value = val;
		                    break;
		                default:
		                    throw new ArgumentOutOfRangeException();
		            }
		        }
		    }

            private double GetValueFromIndex(Facet facet,Term term)
            {
                switch (GetSortOptionsForFacet(facet))
                {
                    case SortOptions.String:
                    case SortOptions.StringVal:
                    case SortOptions.Byte:
                    case SortOptions.Short:
                    case SortOptions.Custom:
                        throw new InvalidOperationException("Cannot translate value with sort option: String");
                    case SortOptions.None:
                    case SortOptions.Int:
                        return NumericUtils.PrefixCodedToInt(term.Text);
                    case SortOptions.Float:
                        return NumericUtils.PrefixCodedToFloat(term.Text);
                    case SortOptions.Long:
                        return NumericUtils.PrefixCodedToLong(term.Text);
                    case SortOptions.Double:
                        return NumericUtils.PrefixCodedToDouble(term.Text);
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

		    private readonly Dictionary<string, SortOptions> cache = new Dictionary<string, SortOptions>();
		    private SortOptions GetSortOptionsForFacet(Facet facet)
		    {
		        SortOptions value;
		        if (cache.TryGetValue(facet.AggregationField, out value))
		            return value;

		        if (indexDefinition.SortOptions.TryGetValue(facet.AggregationField, out value) == false)
		        {
		            if (facet.AggregationField.EndsWith("_Range"))
		            {
		                var fieldWithNoRange = facet.AggregationField.Substring(0, facet.AggregationField.Length - "_Range".Length);
		                if (indexDefinition.SortOptions.TryGetValue(fieldWithNoRange, out value) == false)
		                    value = SortOptions.None;
		            }
		            else
		            {
		                value = SortOptions.None;
		            }
		        }
		        cache[facet.AggregationField] = value;
		        return value;
		    }

		    private static double GetDefaultValue(Facet facet)
		    {
		        double val;
		        switch (facet.Aggregation)
		        {
		            case FacetAggregation.Average:
		            case FacetAggregation.Sum:
		            case FacetAggregation.Count:
		                val = 0;
		                break;
		            case FacetAggregation.Max:
		                val = double.MinValue;
		                break;
		            case FacetAggregation.Min:
		                val = double.MaxValue;
		                break;
		            default:
		                throw new ArgumentOutOfRangeException();
		        }
		        return val;
		    }

		    private void UpdateValue(FacetValue facetValue, Facet value, int docId)
		    {
		        facetValue.Hits++;
		        if (value.Aggregation == FacetAggregation.Count)
		        {
		            facetValue.Value = facetValue.Hits;
		            return;
		        }
		        HashSet<int> set;
		        if (matches.TryGetValue(facetValue, out set) == false)
		        {
		            matches[facetValue] = set = new HashSet<int>();
		        }
		        set.Add(docId);
		    }


		    private void UpdateFacetResults(Dictionary<string, Dictionary<string, FacetValue>> facetsByName)
			{
				foreach (var facet in Facets.Values)
				{
				    if (facet.Mode == FacetMode.Ranges)
				        continue;

					var values = new List<FacetValue>();
					List<string> allTerms;

					int maxResults = Math.Min(PageSize ?? facet.MaxResults ?? Database.Configuration.MaxPageSize, Database.Configuration.MaxPageSize);
					var groups = facetsByName.GetOrDefault(facet.Name);

					if (groups == null)
						continue;

					switch (facet.TermSortMode)
					{
						case FacetTermSortMode.ValueAsc:
							allTerms = new List<string>(groups.OrderBy(x => x.Key).ThenBy(x => x.Value.Hits).Select(x => x.Key));
							break;
						case FacetTermSortMode.ValueDesc:
							allTerms = new List<string>(groups.OrderByDescending(x => x.Key).ThenBy(x => x.Value.Hits).Select(x => x.Key));
							break;
						case FacetTermSortMode.HitsAsc:
							allTerms = new List<string>(groups.OrderBy(x => x.Value.Hits).ThenBy(x => x.Key).Select(x => x.Key));
							break;
						case FacetTermSortMode.HitsDesc:
							allTerms = new List<string>(groups.OrderByDescending(x => x.Value.Hits).ThenBy(x => x.Key).Select(x => x.Key));
							break;
						default:
							throw new ArgumentException(string.Format("Could not understand '{0}'", facet.TermSortMode));
					}

					foreach (var term in allTerms.Skip(Start).TakeWhile(term => values.Count < maxResults))
					{
					    var facetValue = groups.GetOrDefault(term);
					    values.Add(facetValue ?? new FacetValue{Range = term});
					}

				    var previousHits = allTerms.Take(Start).Sum(allTerm =>
					{
					    var facetValue = groups.GetOrDefault(allTerm);
					    return facetValue == null ? 0 : facetValue.Hits;
					});
					Results.Results[facet.Name] = new FacetResult
					{
						Values = values,
						RemainingTermsCount = allTerms.Count - (Start + values.Count),
						RemainingHits = groups.Values.Sum(x=>x.Hits) - (previousHits + values.Sum(x => x.Hits)),
					};

					if (facet.IncludeRemainingTerms)
						Results.Results[facet.Name].RemainingTerms = allTerms.Skip(Start + values.Count).ToList();
				}
			}
		}
	}


}
