﻿#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.Indexes.Analysis;
using Raven.Server.Documents.Indexes.Configuration;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.ServerWide;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Platform;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Config.Categories
{
    [ConfigurationCategory(ConfigurationCategoryType.Indexing)]
    public class IndexingConfiguration : ConfigurationCategory
    {
        private bool? _runInMemory;

        private readonly RavenConfiguration _root;

        private PathSetting _indexStoragePath;

        public IndexingConfiguration(RavenConfiguration root)
        {
            _root = root;

            MaximumSizePerSegment = new Size(PlatformDetails.Is32Bits ? 128 : 1024, SizeUnit.Megabytes);
            LargeSegmentSizeToMerge = new Size(PlatformDetails.Is32Bits ? 16 : 32, SizeUnit.Megabytes);

            var totalMem = MemoryInformation.TotalPhysicalMemory;

            Size defaultEncryptedTransactionSizeLimit;

            if (PlatformDetails.Is32Bits || _root.Storage.ForceUsing32BitsPager || totalMem <= new Size(1, SizeUnit.Gigabytes))
                defaultEncryptedTransactionSizeLimit = new Size(96, SizeUnit.Megabytes);
            else if (totalMem <= new Size(4, SizeUnit.Gigabytes))
                defaultEncryptedTransactionSizeLimit = new Size(128, SizeUnit.Megabytes);
            else if (totalMem <= new Size(16, SizeUnit.Gigabytes))
                defaultEncryptedTransactionSizeLimit = new Size(256, SizeUnit.Megabytes);
            else if (totalMem <= new Size(64, SizeUnit.Gigabytes))
                defaultEncryptedTransactionSizeLimit = new Size(512, SizeUnit.Megabytes);
            else
                defaultEncryptedTransactionSizeLimit = new Size(1024, SizeUnit.Megabytes);

            EncryptedTransactionSizeLimit = defaultEncryptedTransactionSizeLimit;
        }

        [DefaultValue(false)]
        [Description("Whether the indexes should run purely in memory. When running in memory, nothing is written to disk and if the server is restarted all data will be lost. This is mostly useful for testing.")]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.RunInMemory", ConfigurationEntryScope.ServerWideOrPerDatabase, setDefaultValueIfNeeded: false)]
        public virtual bool RunInMemory
        {
            get
            {
                if (_runInMemory == null)
                    _runInMemory = _root.Core.RunInMemory;

                return _runInMemory.Value;
            }

            protected set => _runInMemory = value;
        }

        [DefaultValue(false)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.Disable", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public virtual bool Disabled { get; protected set; }

        [Description("The default deployment mode for static indexes")]
        [DefaultValue(IndexDeploymentMode.Parallel)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.Static.DeploymentMode", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public IndexDeploymentMode StaticIndexDeploymentMode { get; protected set; }

        [Description("The default deployment mode for auto indexes")]
        [DefaultValue(IndexDeploymentMode.Parallel)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.Auto.DeploymentMode", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public IndexDeploymentMode AutoIndexDeploymentMode { get; protected set; }

        [Description("Indicate if indexing performance metrics are gathered")]
        [DefaultValue(true)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.Metrics.Enabled", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public virtual bool EnableMetrics { get; protected set; }

        [ReadOnlyPath]
        public virtual PathSetting StoragePath => _indexStoragePath ??= _root.ResourceType == ResourceType.Server ? null : _root.Core.DataDirectory.Combine("Indexes");

        [DefaultValue(null)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.TempPath", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public virtual PathSetting TempPath { get; protected set; }

        [Description("How long indexing will keep document transaction open when indexing. After this the transaction will be reopened.")]
        [DefaultValue(15)]
        [TimeUnit(TimeUnit.Seconds)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MaxTimeForDocumentTransactionToRemainOpenInSec", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting MaxTimeForDocumentTransactionToRemainOpen { get; protected set; }

        [Description("How long should we keep a superseded auto index?")]
        [DefaultValue(15)]
        [TimeUnit(TimeUnit.Seconds)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.TimeBeforeDeletionOfSupersededAutoIndexInSec", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public TimeSetting TimeBeforeDeletionOfSupersededAutoIndex { get; protected set; }

        [Description("How long the database should wait before marking an auto index with the idle flag")]
        [DefaultValue(30)]
        [TimeUnit(TimeUnit.Minutes)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.TimeToWaitBeforeMarkingAutoIndexAsIdleInMin", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public TimeSetting TimeToWaitBeforeMarkingAutoIndexAsIdle { get; protected set; }

        [Description("EXPERT ONLY")]
        [DefaultValue(false)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.DisableQueryOptimizerGeneratedIndexes", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public bool DisableQueryOptimizerGeneratedIndexes { get; protected set; }

        [Description("How long the database should wait before deleting an auto index with the idle flag")]
        [DefaultValue(72)]
        [TimeUnit(TimeUnit.Hours)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.TimeToWaitBeforeDeletingAutoIndexMarkedAsIdleInHrs", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public TimeSetting TimeToWaitBeforeDeletingAutoIndexMarkedAsIdle { get; protected set; }

        [Description("EXPERT ONLY")]
        [DefaultValue(512)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MinNumberOfMapAttemptsAfterWhichBatchWillBeCanceledIfRunningLowOnMemory", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int MinNumberOfMapAttemptsAfterWhichBatchWillBeCanceledIfRunningLowOnMemory { get; protected set; }

        [Description("EXPERT ONLY")]
        [DefaultValue(2)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.NumberOfConcurrentStoppedBatchesIfRunningLowOnMemory", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int NumberOfConcurrentStoppedBatchesIfRunningLowOnMemory { get; protected set; }

        [Description("Number of seconds after which mapping will end even if there is more to map. By default we will map everything we can in single batch.")]
        [DefaultValue(-1)]
        [TimeUnit(TimeUnit.Seconds)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MapTimeoutInSec", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting MapTimeout { get; protected set; }

        [Description("Maximum number of mapped documents. Cannot be less than 128. By default 'null' - no limit.")]
        [DefaultValue(null)]
        [MinValue(128)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MapBatchSize", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int? MapBatchSize { get; protected set; }

        [Description("Number of minutes after which mapping will end even if there is more to map. This will only be applied if we pass the last etag in collection that we saw when batch was started.")]
        [DefaultValue(15)]
        [TimeUnit(TimeUnit.Minutes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MapTimeoutAfterEtagReachedInMin", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting MapTimeoutAfterEtagReached { get; protected set; }

        [Description("EXPERT: the type of JavaScript engine that will be used by RavenDB: 'Jint'  or 'V8'")]
        [DefaultValue(null)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.EngineForScript", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public JavaScriptEngineType? JsEngineType { get; set; }

        [Description("EXPERT: Enables Strict Mode in JavaScript engine. Default: true")]
        [DefaultValue(null)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.StrictModeForScript", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public bool? JsStrictMode { get; set; }

        [Description("EXPERT: Maximum number of steps in the JS script execution (Jint)")]
        [DefaultValue(null)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.MaxStepsForScript", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int? JsMaxSteps { get; set; }

        [Description("EXPERT: Maximum duration in milliseconds of the JS script execution (V8)")]  // TODO [shlomo] To expose in Jint TimeConstraint2 class (as it was made to MaxStatements)
        [TimeUnit(TimeUnit.Milliseconds)]
        [DefaultValue(null)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.MaxDurationForScript", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting? JsMaxDuration { get; set; }

        [Description("Time (in minutes) between index cleanup")]
        [DefaultValue(10)]
        [TimeUnit(TimeUnit.Minutes)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.CleanupIntervalInMin", ConfigurationEntryScope.ServerWideOnly)]
        public TimeSetting CleanupInterval { get; set; }

        [Description("Smallest n-gram to generate when NGram analyzer is used")]
        [DefaultValue(2)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.Analyzers.NGram.MinGram", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int MinGram { get; set; }

        [Description("Largest n-gram to generate when NGram analyzer is used")]
        [DefaultValue(6)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.Analyzers.NGram.MaxGram", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int MaxGram { get; set; }

        [Description("Managed allocations limit in an indexing batch after which the batch will complete and an index will continue by starting a new one")]
        [DefaultValue(2048)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.ManagedAllocationsBatchSizeLimitInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size? ManagedAllocationsBatchLimit { get; protected set; }

        [Description("Expert: The maximum size in MB that we'll consider for segments merging")]
        [DefaultValue(DefaultValueSetInConstructor)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MaximumSizePerSegmentInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size MaximumSizePerSegment { get; protected set; }

        [Description("Expert: How often segment indices are merged. With smaller values, less RAM is used while indexing, and searches on unoptimized indices are faster, but indexing speed is slower")]
        [DefaultValue(10)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MergeFactor", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int MergeFactor { get; protected set; }

        [Description("Expert: The definition of a large segment in MB. We wont merge more than " + nameof(NumberOfLargeSegmentsToMergeInSingleBatch) + " in a single batch")]
        [DefaultValue(DefaultValueSetInConstructor)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.LargeSegmentSizeToMergeInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size LargeSegmentSizeToMerge { get; protected set; }

        [Description("Expert: Number of large segments (defined by " + nameof(LargeSegmentSizeToMerge) + ") to merge in a single batch")]
        [DefaultValue(2)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.NumberOfLargeSegmentsToMergeInSingleBatch", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public int NumberOfLargeSegmentsToMergeInSingleBatch { get; protected set; }

        [Description("Expert: How long will we let merges to run before we close the transaction")]
        [DefaultValue(15)]
        [TimeUnit(TimeUnit.Seconds)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.MaxTimeForMergesToKeepRunningInSec", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting MaxTimeForMergesToKeepRunning { get; protected set; }

        [Description("Transaction size limit after which an index will stop and complete the current batch")]
        [DefaultValue(null)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.TransactionSizeLimitInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size? TransactionSizeLimit { get; protected set; }

        [Description("Transaction size limit, for encrypted database only, after which an index will stop and complete the current batch")]
        [DefaultValue(DefaultValueSetInConstructor)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.Encrypted.TransactionSizeLimitInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size? EncryptedTransactionSizeLimit { get; protected set; }

        [Description("Amount of scratch space that we allow to use for the index storage. After exceeding this limit the current indexing batch will complete and the index will force flush and sync storage environment.")]
        [DefaultValue(null)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.ScratchSpaceLimitInMb", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public Size? ScratchSpaceLimit { get; protected set; }

        [Description("Maximum amount of scratch space that we allow to use for all index storages per server. After exceeding this limit the indexes will complete their current indexing batches and force flush and sync storage environments.")]
        [DefaultValue(null)]
        [SizeUnit(SizeUnit.Megabytes)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.GlobalScratchSpaceLimitInMb", ConfigurationEntryScope.ServerWideOnly)]
        public Size? GlobalScratchSpaceLimit { get; protected set; }

        [Description("Max time to wait when forcing the storage environment flush and sync after exceeding scratch space limit")]
        [DefaultValue(30)]
        [TimeUnit(TimeUnit.Seconds)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.MaxTimeToWaitAfterFlushAndSyncWhenExceedingScratchSpaceLimit", ConfigurationEntryScope.ServerWideOnly)]
        public TimeSetting MaxTimeToWaitAfterFlushAndSyncWhenExceedingScratchSpaceLimit { get; protected set; }

        [Description("Indicates if missing fields should be indexed same as 'null' values or not. Default: false")]
        [DefaultValue(false)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.IndexMissingFieldsAsNull", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public bool IndexMissingFieldsAsNull { get; set; }

        [Description("Indicates if empty index entries should be indexed by static indexes. Default: false")]
        [DefaultValue(false)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.IndexEmptyEntries", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public bool IndexEmptyEntries { get; set; }

        [Description("Indicates how error indexes should behave on database startup when they are loaded. By default they are not started.")]
        [DefaultValue(IndexStartupBehavior.Default)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.ErrorIndexStartupBehavior", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public IndexStartupBehavior ErrorIndexStartupBehavior { get; set; }

        [Description("Limits the number of concurrently running and processing indexes on the server. Default: null (no limit)")]
        [DefaultValue(null)]
        [MinValue(1)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.MaxNumberOfConcurrentlyRunningIndexes", ConfigurationEntryScope.ServerWideOnly)]
        public int? MaxNumberOfConcurrentlyRunningIndexes { get; set; }

        [Description("Location of NuGet packages cache")]
        [DefaultValue("Packages/NuGet")]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.NuGetPackagesPath", ConfigurationEntryScope.ServerWideOnly)]
        public PathSetting NuGetPackagesPath { get; set; }

        [Description("Default NuGet source URL")]
        [DefaultValue("https://api.nuget.org/v3/index.json")]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.NuGetPackageSourceUrl", ConfigurationEntryScope.ServerWideOnly)]
        public string NuGetPackageSourceUrl { get; set; }
        
        [Description("Number of index history revisions to keep per index")]
        [DefaultValue(10)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.History.NumberOfRevisions", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int HistoryRevisionsNumber { get; set; }

        [Description("Default analyzer that will be used for fields.")]
        [DefaultValue(Constants.Documents.Indexing.Analyzers.Default)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.Analyzers.Default", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public string DefaultAnalyzer { get; set; }

        [Description("Default analyzer that will be used for exact fields.")]
        [DefaultValue(Constants.Documents.Indexing.Analyzers.DefaultExact)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.Analyzers.Exact.Default", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public string DefaultExactAnalyzer { get; set; }

        [Description("Default analyzer that will be used for search fields.")]
        [DefaultValue(Constants.Documents.Indexing.Analyzers.DefaultSearch)]
        [IndexUpdateType(IndexUpdateType.Reset)]
        [ConfigurationEntry("Indexing.Analyzers.Search.Default", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public string DefaultSearchAnalyzer { get; set; }

        [Description("How long the index should delay processing after new work is detected")]
        [DefaultValue(null)]
        [TimeUnit(TimeUnit.Milliseconds)]
        [IndexUpdateType(IndexUpdateType.Refresh)]
        [ConfigurationEntry("Indexing.Throttling.TimeIntervalInMs", ConfigurationEntryScope.ServerWideOrPerDatabaseOrPerIndex)]
        public TimeSetting? ThrottlingTimeInterval { get; protected set; }

        public Lazy<AnalyzerFactory> DefaultAnalyzerType { get; private set; }

        public Lazy<AnalyzerFactory> DefaultExactAnalyzerType { get; private set; }

        public Lazy<AnalyzerFactory> DefaultSearchAnalyzerType { get; private set; }

        [Description("EXPERT: Allows to open an index without checking if current Database ID matched the one for which index was created.")]
        [DefaultValue(false)]
        [IndexUpdateType(IndexUpdateType.None)]
        [ConfigurationEntry("Indexing.SkipDatabaseIdValidationOnIndexOpening", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public bool SkipDatabaseIdValidationOnIndexOpening { get; set; }

        protected override void ValidateProperty(PropertyInfo property)
        {
            base.ValidateProperty(property);

            var updateTypeAttribute = property.GetCustomAttribute<IndexUpdateTypeAttribute>();
            if (updateTypeAttribute == null)
                throw new InvalidOperationException($"No {nameof(IndexUpdateTypeAttribute)} available for '{property.Name}' property.");
        }

        public override void Initialize(IConfigurationRoot settings, HashSet<string> settingsNames, IConfigurationRoot serverWideSettings, HashSet<string> serverWideSettingsNames, ResourceType type, string resourceName)
        {
            base.Initialize(settings, settingsNames, serverWideSettings, serverWideSettingsNames, type, resourceName);

            InitializeAnalyzers(resourceName);
        }

        public void InitializeAnalyzers(string resourceName)
        {
            DefaultAnalyzerType = new Lazy<AnalyzerFactory>(() => IndexingExtensions.GetAnalyzerType("@default", DefaultAnalyzer, resourceName));
            DefaultExactAnalyzerType = new Lazy<AnalyzerFactory>(() => IndexingExtensions.GetAnalyzerType("@default", DefaultExactAnalyzer, resourceName));
            DefaultSearchAnalyzerType = new Lazy<AnalyzerFactory>(() => IndexingExtensions.GetAnalyzerType("@default", DefaultSearchAnalyzer, resourceName));
        }

        public enum IndexStartupBehavior
        {
            Default,
            Start,
            ResetAndStart
        }
    }
}
