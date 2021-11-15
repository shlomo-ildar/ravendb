using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.JavaScript;
using Raven.Server.Config;
using Raven.Server.Config.Categories;

namespace Raven.Server.Documents.Indexes.Static
{
    public class JavaScriptOptions : IJavaScriptOptions
    {
        public JavaScriptEngineType EngineType { get; set; }
        public bool StrictMode { get; set; }
        public int MaxSteps { get; set; }
        public int MaxDurationInMs { get; set; }

        public JavaScriptOptions(JavaScriptEngineType engineType, bool strictMode, int maxSteps, int maxDurationInMs)
        {
            EngineType = engineType;
            StrictMode = strictMode;
            MaxSteps = maxSteps;
            MaxDurationInMs = maxDurationInMs;
        }

        public JavaScriptOptions(IndexingConfiguration indexConfiguration, RavenConfiguration configuration)
        {
            var patchingConfig = configuration.Patching;
            var jsConfig = configuration.JavaScript;
            
            EngineType = indexConfiguration.JsEngineType ?? jsConfig.EngineType;
            StrictMode = indexConfiguration.JsStrictMode ?? patchingConfig.StrictMode ?? jsConfig.StrictMode; // patching is of priority for backward compatibility
            MaxSteps = indexConfiguration.JsMaxSteps ?? jsConfig.MaxSteps;
            MaxDurationInMs = indexConfiguration.JsMaxDurationInMs ?? jsConfig.MaxDurationInMs;
        }

        public JavaScriptOptions(IJavaScriptOptions indexConfiguration)
        {
            EngineType = indexConfiguration.EngineType;
            StrictMode = indexConfiguration.StrictMode;
            MaxSteps = indexConfiguration.MaxSteps;
            MaxDurationInMs = indexConfiguration.MaxDurationInMs;
        }

        public JavaScriptOptions(JavaScriptOptionsForSmuggler jsOptionsForSmuggler)
        {
            EngineType = jsOptionsForSmuggler.EngineType;
            StrictMode = jsOptionsForSmuggler.StrictMode;
            MaxSteps = jsOptionsForSmuggler.MaxSteps;
            MaxDurationInMs = jsOptionsForSmuggler.MaxDurationInMs;
        }
    }
}
