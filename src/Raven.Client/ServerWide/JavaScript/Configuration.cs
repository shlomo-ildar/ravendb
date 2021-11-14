namespace Raven.Client.ServerWide.JavaScript
{
    public enum JavaScriptEngineType : byte
    {
        Jint = 1,
        V8 = 2
    }
    
    public interface IJavaScriptOptions
    {
        JavaScriptEngineType EngineType { get; set; }
        bool StrictMode { get; set; }
        int MaxSteps { get; set; }
        int MaxDurationInMs { get; set; }
    }
}
