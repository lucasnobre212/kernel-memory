﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticMemory.Core.Configuration;
using Microsoft.SemanticMemory.Core.Diagnostics;

namespace Microsoft.SemanticMemory.Core.MemoryStorage.AzureCognitiveSearch;

public class AzureCognitiveSearchMemory : ISemanticMemoryVectorDb
{
    private readonly string _indexPrefix;
    private readonly ILogger _log;

    public AzureCognitiveSearchMemory(
        string endpoint,
        string apiKey,
        string indexPrefix = "",
        ILogger? log = null)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new ConfigurationException("Azure Cognitive Search Endpoint is empty");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new ConfigurationException("Azure Cognitive Search API key is empty");
        }

        this._indexPrefix = indexPrefix;
        this._log = log ?? DefaultLogger<AzureCognitiveSearchMemory>.Instance;

        AzureKeyCredential credentials = new(apiKey);
        this._adminClient = new SearchIndexClient(new Uri(endpoint), credentials, GetClientOptions());
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(string indexName, VectorDbSchema schema, CancellationToken cancellationToken = default)
    {
        if (await this.DoesIndexExistAsync(indexName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var indexSchema = this.PrepareIndexSchema(indexName, schema);

        try
        {
            await this._adminClient.CreateIndexAsync(indexSchema, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            this._log.LogWarning("Index already exists, nothing to do: {0}", e.Message);
        }
    }

    /// <inheritdoc />
    public Task DeleteIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        return this._adminClient.DeleteIndexAsync(this.NormalizeIndexName(indexName), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(string indexName, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(indexName);
        AzureCognitiveSearchMemoryRecord localRecord = AzureCognitiveSearchMemoryRecord.FromMemoryRecord(record);

        await client.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[] { localRecord }),
            new IndexDocumentsOptions { ThrowOnAnyError = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return record.Id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(MemoryRecord, double)> GetNearestMatchesAsync(
        string indexName,
        Embedding<float> embedding,
        int limit,
        double minRelevanceScore = 0,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(indexName);

        SearchQueryVector vectorQuery = new()
        {
            KNearestNeighborsCount = limit,
            Fields = AzureCognitiveSearchMemoryRecord.VectorField,
            Value = embedding.Vector.ToList()
        };

        SearchOptions options = new() { Vector = vectorQuery };
        Response<SearchResults<AzureCognitiveSearchMemoryRecord>>? searchResult = null;
        try
        {
            searchResult = await client
                .SearchAsync<AzureCognitiveSearchMemoryRecord>(null, options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            this._log.LogWarning("Not found: {0}", e.Message);
            // Index not found, no data to return
        }

        if (searchResult == null) { yield break; }

        await foreach (SearchResult<AzureCognitiveSearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync())
        {
            if (doc == null || doc.Score < minRelevanceScore) { continue; }

            MemoryRecord memoryRecord = doc.Document.ToMemoryRecord(withEmbeddings);

            yield return (memoryRecord, doc.Score ?? 0);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string indexName, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        string id = AzureCognitiveSearchMemoryRecord.FromMemoryRecord(record).Id;
        var client = this.GetSearchClient(indexName);

        try
        {
            this._log.LogDebug("Deleting record {0} from index {1}", id, indexName);
            Response<IndexDocumentsResult>? result = await client.DeleteDocumentsAsync("id", new List<string> { id }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            this._log.LogTrace("Delete response status: {0}, content: {1}", result.GetRawResponse().Status, result.GetRawResponse().Content.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            this._log.LogTrace("Index {0} record {1} not found, nothing to delete", indexName, id);
        }
    }

    // /// <inheritdoc />
    // private async IAsyncEnumerable<MemoryRecord> SearchByFieldValueAsync(
    //     string indexName,
    //     string fieldName,
    //     bool fieldIsCollection,
    //     string fieldValue,
    //     int limit,
    //     bool withEmbeddings = false,
    //     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    // {
    //     var client = this.GetSearchClient(indexName);
    //
    //     fieldValue = fieldValue.Replace("'", "''");
    //
    //     // See: https://learn.microsoft.com/en-us/azure/search/search-query-understand-collection-filters
    //     var options = new SearchOptions
    //     {
    //         Filter = SearchFilter.Create(fieldIsCollection
    //             ? (FormattableString)$"{fieldName} eq '{fieldValue}'"
    //             : (FormattableString)$"{fieldName}/any(s: s eq '{fieldValue}')")
    //     };
    //
    //     Response<SearchResults<AzureCognitiveSearchMemoryRecord>>? searchResult = null;
    //     try
    //     {
    //         searchResult = await client
    //             .SearchAsync<AzureCognitiveSearchMemoryRecord>(null, options, cancellationToken: cancellationToken)
    //             .ConfigureAwait(false);
    //     }
    //     catch (RequestFailedException e) when (e.Status == 404)
    //     {
    //         this._log.LogWarning("Not found: {0}", e.Message);
    //         // Index not found, no data to return
    //     }
    //
    //     if (searchResult == null) { yield break; }
    //
    //     await foreach (SearchResult<AzureCognitiveSearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync())
    //     {
    //         yield return doc.Document.ToMemoryRecord(withEmbeddings);
    //     }
    // }

    #region private

    // private async Task<AzureCognitiveSearchMemoryRecord?> GetAsync(string indexName, string id, CancellationToken cancellationToken = default)
    // {
    //     try
    //     {
    //         Response<AzureCognitiveSearchMemoryRecord>? result = await this.GetSearchClient(indexName)
    //             .GetDocumentAsync<AzureCognitiveSearchMemoryRecord>(id, cancellationToken: cancellationToken)
    //             .ConfigureAwait(false);
    //
    //         return result?.Value;
    //     }
    //     catch (Exception e)
    //     {
    //         this._log.LogError(e, "Failed to fetch record");
    //         return null;
    //     }
    // }

    private async Task<bool> DoesIndexExistAsync(string indexName, CancellationToken cancellationToken = default)
    {
        string normalizeIndexName = this.NormalizeIndexName(indexName);

        return await this.GetIndexesAsync(cancellationToken)
            .AnyAsync(index => string.Equals(index, normalizeIndexName, StringComparison.OrdinalIgnoreCase),
                cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<string> UpsertBatchAsync(
        string indexName,
        IEnumerable<MemoryRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = this.GetSearchClient(indexName);

        foreach (MemoryRecord record in records)
        {
            var localRecord = AzureCognitiveSearchMemoryRecord.FromMemoryRecord(record);
            await client.IndexDocumentsAsync(
                IndexDocumentsBatch.Upload(new[] { localRecord }),
                new IndexDocumentsOptions { ThrowOnAnyError = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            yield return record.Id;
        }
    }

    /// <summary>
    /// Index names cannot contain special chars. We use this rule to replace a few common ones
    /// with an underscore and reduce the chance of errors. If other special chars are used, we leave it
    /// to the service to throw an error.
    /// Note:
    /// - replacing chars introduces a small chance of conflicts, e.g. "the-user" and "the_user".
    /// - we should consider whether making this optional and leave it to the developer to handle.
    /// </summary>
    private static readonly Regex s_replaceIndexNameSymbolsRegex = new(@"[\s|\\|/|.|_|:]");

    private readonly ConcurrentDictionary<string, SearchClient> _clientsByIndex = new();

    private readonly SearchIndexClient _adminClient;

    /// <summary>
    /// Get a search client for the index specified.
    /// Note: the index might not exist, but we avoid checking everytime and the extra latency.
    /// </summary>
    /// <param name="indexName">Index name</param>
    /// <returns>Search client ready to read/write</returns>
    private SearchClient GetSearchClient(string indexName)
    {
        var normalIndexName = this.NormalizeIndexName(indexName);
        this._log.LogTrace("Preparing search client, index name '{0}' normalized to '{1}'", indexName, normalIndexName);

        // Search an available client from the local cache
        if (!this._clientsByIndex.TryGetValue(normalIndexName, out SearchClient? client))
        {
            client = this._adminClient.GetSearchClient(normalIndexName);
            this._clientsByIndex[normalIndexName] = client;
        }

        return client;
    }

    private async IAsyncEnumerable<string> GetIndexesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var indexesAsync = this._adminClient.GetIndexesAsync(cancellationToken).ConfigureAwait(false);
        await foreach (SearchIndex? index in indexesAsync)
        {
            yield return index.Name;
        }
    }

    private static void ValidateSchema(VectorDbSchema schema)
    {
        schema.Validate(vectorSizeRequired: true);

        foreach (var f in schema.Fields.Where(x => x.Type == VectorDbField.FieldType.Vector))
        {
            if (f.VectorMetric is not (VectorDbField.VectorMetricType.Cosine or VectorDbField.VectorMetricType.Euclidean or VectorDbField.VectorMetricType.DotProduct))
            {
                throw new AzureCognitiveSearchMemoryException($"Vector metric '{f.VectorMetric:G}' not supported");
            }
        }
    }

    /// <summary>
    /// Options used by the Azure Cognitive Search client, e.g. User Agent.
    /// See also https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/core/Azure.Core/src/DiagnosticsOptions.cs
    /// </summary>
    private static SearchClientOptions GetClientOptions()
    {
        return new SearchClientOptions
        {
            Diagnostics =
            {
                IsTelemetryEnabled = Telemetry.IsTelemetryEnabled,
                ApplicationId = Telemetry.HttpUserAgent,
            },
        };
    }

    /// <summary>
    /// Normalize index name to match ACS rules.
    /// The method doesn't handle all the error scenarios, leaving it to the service
    /// to throw an error for edge cases not handled locally.
    /// </summary>
    /// <param name="indexName">Value to normalize</param>
    /// <returns>Normalized name</returns>
    private string NormalizeIndexName(string indexName)
    {
        indexName = $"{this._indexPrefix}{indexName}";

        if (indexName.Length > 128)
        {
            throw new AzureCognitiveSearchMemoryException("The index name (prefix included) is too long, it cannot exceed 128 chars.");
        }

#pragma warning disable CA1308 // The service expects a lowercase string
        indexName = indexName.ToLowerInvariant();
#pragma warning restore CA1308

        indexName = s_replaceIndexNameSymbolsRegex.Replace(indexName.Trim(), "-");

        // Name cannot start with a dash
        if (indexName.StartsWith('-')) { indexName = $"z{indexName}"; }

        // Name cannot end with a dash
        if (indexName.EndsWith('-')) { indexName = $"{indexName}z"; }

        return indexName;
    }

    private SearchIndex PrepareIndexSchema(string indexName, VectorDbSchema schema)
    {
        ValidateSchema(schema);

        indexName = this.NormalizeIndexName(indexName);

        const string VectorSearchConfigName = "SemanticMemoryDefaultCosine";

        var indexSchema = new SearchIndex(indexName)
        {
            Fields = new List<SearchField>(),
            VectorSearch = new VectorSearch
            {
                AlgorithmConfigurations =
                {
                    new HnswVectorSearchAlgorithmConfiguration(VectorSearchConfigName)
                    {
                        Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine }
                    }
                }
            }
        };

        /* Field attributes: see https://learn.microsoft.com/en-us/azure/search/search-what-is-an-index
         * - searchable: Full-text searchable, subject to lexical analysis such as word-breaking during indexing.
         * - filterable: Filterable fields of type Edm.String or Collection(Edm.String) don't undergo word-breaking.
         * - facetable: Used for counting. Fields of type Edm.String that are filterable, "sortable", or "facetable" can be at most 32kb. */
        SearchField? vectorField = null;
        foreach (var field in schema.Fields)
        {
            switch (field.Type)
            {
                case VectorDbField.FieldType.Unknown:
                default:
                    throw new AzureCognitiveSearchMemoryException($"Unsupported field type {field.Type:G}");

                case VectorDbField.FieldType.Vector:
                    vectorField = new SearchField(field.Name, SearchFieldDataType.Collection(SearchFieldDataType.Single))
                    {
                        IsKey = false,
                        IsFilterable = false,
                        IsSearchable = true,
                        IsFacetable = false,
                        IsSortable = false,
                        VectorSearchDimensions = field.VectorSize,
                        VectorSearchConfiguration = VectorSearchConfigName,
                    };

                    break;
                case VectorDbField.FieldType.Text:
                    var useBugWorkAround = true;
                    if (useBugWorkAround)
                    {
                        /* August 2023:
                           - bug: Indexes must have a searchable string field
                           - temporary workaround: make the key field searchable

                         Example of unexpected error:
                            Date: Tue, 01 Aug 2023 23:15:59 GMT
                            Status: 400 (Bad Request)
                            ErrorCode: OperationNotAllowed

                            Content:
                            {"error":{"code":"OperationNotAllowed","message":"If a query contains the search option the
                            target index must contain one or more searchable string fields.\r\nParameter name: search",
                            "details":[{"code":"CannotSearchWithoutSearchableFields","message":"If a query contains the
                            search option the target index must contain one or more searchable string fields."}]}}

                            at Azure.Search.Documents.SearchClient.SearchInternal[T](SearchOptions options,
                            String operationName, Boolean async, CancellationToken cancellationToken)
                         */
                        indexSchema.Fields.Add(new SearchField(field.Name, SearchFieldDataType.String)
                        {
                            IsKey = field.IsKey,
                            IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                            IsFacetable = false,
                            IsSortable = false,
                            IsSearchable = true,
                        });
                    }
                    else
                    {
                        indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.String)
                        {
                            IsKey = field.IsKey,
                            IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                            IsFacetable = false,
                            IsSortable = false,
                        });
                    }

                    break;

                case VectorDbField.FieldType.Integer:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Int64)
                    {
                        IsKey = field.IsKey,
                        IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case VectorDbField.FieldType.Decimal:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Double)
                    {
                        IsKey = field.IsKey,
                        IsFilterable = field.IsKey || field.IsFilterable, // Filterable keys are recommended for batch operations
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case VectorDbField.FieldType.Bool:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Boolean)
                    {
                        IsKey = false,
                        IsFilterable = field.IsFilterable,
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;

                case VectorDbField.FieldType.ListOfStrings:
                    indexSchema.Fields.Add(new SimpleField(field.Name, SearchFieldDataType.Collection(SearchFieldDataType.String))
                    {
                        IsKey = false,
                        IsFilterable = field.IsFilterable,
                        IsFacetable = false,
                        IsSortable = false,
                    });
                    break;
            }
        }

        // Add the vector field as the last element, so Azure Portal shows
        // the other fields before the long list of floating numbers
        indexSchema.Fields.Add(vectorField);

        return indexSchema;
    }

    #endregion
}
