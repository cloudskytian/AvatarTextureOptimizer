// 编译验证桩：NDMF 1.14.4 公开 API（依据已读源码）/ Compile-check stubs: NDMF 1.14.4 public API (from the source we read).
// 仅覆盖 ATO 代码使用的成员。Not shipped with the package.

using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace nadena.dev.ndmf
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class ExportsPluginAttribute : Attribute
    {
        public ExportsPluginAttribute(Type pluginType) { }
    }

    public enum BuildPhase
    {
        Resolving = 0,
        Generating = 1,
        Transforming = 2,
        Optimizing = 3,
    }

    public abstract class PluginBase
    {
        public abstract string QualifiedName { get; }
        public abstract string DisplayName { get; }
    }

    public abstract class Plugin<T> : PluginBase where T : Plugin<T>, new()
    {
        public static Plugin<T> Instance { get; } = new T();
        protected abstract void Configure();
        protected Sequence InPhase(BuildPhase phase) => new Sequence();
        protected virtual void OnUnhandledException(Exception e) { }
    }

    public class Sequence
    {
        public Sequence AfterPlugin(string qualifiedName) => this;
        public Sequence BeforePlugin(string qualifiedName) => this;
        public Sequence Run(string name, Action<BuildContext> action) => this;
        public Sequence Run(Pass pass) => this;
        public Sequence Then => this;
        public Sequence BeforePass(Pass pass) => this;
        public Sequence AfterPass(Pass pass) => this;
        public Sequence WithRequiredExtensions(Type[] types, Action<Sequence> action) => this;
    }

    public class Pass { }

    public sealed class BuildContext
    {
        public GameObject AvatarRootObject { get; } = new GameObject();
        public Transform AvatarRootTransform { get; }
        public ObjectRegistry ObjectRegistry { get; }
        public IAssetSaver AssetSaver { get; }
        public UnityEngine.Object AssetContainer { get; }
        public ErrorReport ErrorReport { get; }
        public bool Successful { get; }
        public T GetState<T>() where T : new() => new T();
        public T GetState<T>(Func<BuildContext, T> init) => init(this);
        public bool IsTemporaryAsset(UnityEngine.Object obj) => false;
        public void SetEnableUVDistributionRecalculation(Mesh mesh, bool enabled) { }
    }

    public interface IAssetSaver
    {
        bool IsTemporaryAsset(UnityEngine.Object obj);
    }

    public sealed class ObjectRegistry
    {
        public static IObjectRegistry ActiveRegistry { get; }
        public static ObjectReference GetReference(UnityEngine.Object obj) => null;
        public static ObjectReference RegisterReplacedObject(UnityEngine.Object oldObject, UnityEngine.Object newObject) => null;
        public static ObjectReference RegisterReplacedObject(ObjectReference oldObject, UnityEngine.Object newObject) => null;
    }

    public interface IObjectRegistry
    {
        ObjectReference GetReference(UnityEngine.Object obj, bool create = true);
        ObjectReference RegisterReplacedObject(UnityEngine.Object oldObject, UnityEngine.Object newObject);
    }

    public class ObjectReference
    {
        public UnityEngine.Object Object;
        public ObjectReference Reference;
    }

    public enum ErrorSeverity
    {
        Information = 0,
        NonFatal = 1,
        Error = 2,
        InternalError = 3,
    }

    public interface IError
    {
        ErrorSeverity Severity { get; }
        VisualElement CreateVisualElement(ErrorReport report);
        string ToMessage();
        void AddReference(ObjectReference reference);
    }

    public sealed class ErrorReport
    {
        public static void ReportError(IError error) { }
        public static void ReportError(Localizer localizer, ErrorSeverity errorSeverity, string key, params object[] args) { }
        public static void ReportException(Exception e, string additionalStackTrace = null) { }
        public string AvatarName { get; }
        public System.Collections.Generic.List<object> Errors { get; }
    }

    public class Localizer { }
}

namespace nadena.dev.ndmf.localization
{
    public static class LanguagePrefs
    {
        public static string Language => "en-us";
    }
}
