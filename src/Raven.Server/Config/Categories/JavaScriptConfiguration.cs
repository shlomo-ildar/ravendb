using System.ComponentModel;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Platform;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Config.Categories
{
    public interface IJavaScriptOptions
    {
        JavaScriptEngineType EngineType { get; set; }
        bool StrictMode { get; set; }
        int MaxSteps { get; set; }
        TimeSetting MaxDuration { get; set; }
    }
    
    [ConfigurationCategory(ConfigurationCategoryType.JavaScript)]
    public class JavaScriptConfiguration : ConfigurationCategory, IJavaScriptOptions
    {
        [Description("EXPERT: the type of JavaScript engine that will be used by RavenDB: 'Jint'  or 'V8'")]
        [DefaultValue(JavaScriptEngineType.Jint)]
        [ConfigurationEntry("JsConfiguration.Engine", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public JavaScriptEngineType EngineType { get; set; }

        [Description("EXPERT: Enables Strict Mode in JavaScript engine. Default: true")]
        [DefaultValue(true)]
        [ConfigurationEntry("JsConfiguration.StrictMode", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public bool StrictMode { get; set; }

        [Description("EXPERT: Maximum number of steps in the JS script execution (Jint)")]
        [DefaultValue(10_000)]
        [ConfigurationEntry("JsConfiguration.MaxSteps", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int MaxSteps { get; set; }

        [Description("EXPERT: Maximum duration in milliseconds of the JS script execution (V8)")]  // TODO In Jint TimeConstraint2 is the internal class so the approach applied to MaxStatements doesn't work here
        [DefaultValue(1000)] // TODO [shlomo] may be decreased when tests get stable
        [TimeUnit(TimeUnit.Milliseconds)]
        [ConfigurationEntry("JsConfiguration.MaxDuration", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public TimeSetting MaxDuration { get; set; }
    }
}
