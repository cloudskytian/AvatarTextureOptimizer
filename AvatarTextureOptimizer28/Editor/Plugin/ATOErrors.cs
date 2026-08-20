using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace net.fosa.ato.editor
{
    /// <summary>EN: Base for all ATO errors, wired to our localizer. ZH: 所有 ATO 错误的基类，接入我们的本地化器。</summary>
    public abstract class ATOError : SimpleError
    {
        /// <inheritdoc/>
        public override Localizer Localizer => ATOLocalizer.L;
    }

    /// <summary>EN: More than one component on the avatar. ZH: Avatar 上存在多个组件。</summary>
    public sealed class MultipleComponentsError : ATOError
    {
        /// <inheritdoc/>
        public override string TitleKey => "ato.error.multipleComponents";
        /// <inheritdoc/>
        public override ErrorSeverity Severity => ErrorSeverity.Error;

        /// <summary>EN: Construct, referencing every offending component. ZH: 构造，并引用所有违规组件。</summary>
        public MultipleComponentsError(IEnumerable<AvatarTextureOptimizer> components)
        {
            foreach (var c in components) AddReference(ObjectRegistry.GetReference(c));
        }
    }

    /// <summary>EN: Component is not on a VRCAvatarDescriptor object. ZH: 组件未挂在带 VRCAvatarDescriptor 的对象上。</summary>
    public sealed class MissingDescriptorError : ATOError
    {
        /// <inheritdoc/>
        public override string TitleKey => "ato.error.noDescriptor";
        /// <inheritdoc/>
        public override ErrorSeverity Severity => ErrorSeverity.Error;

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public MissingDescriptorError(AvatarTextureOptimizer component)
        {
            if (component != null) AddReference(ObjectRegistry.GetReference(component));
        }
    }

    /// <summary>EN: A generic warning surfaced in the NDMF console. ZH: 呈现在 NDMF 控制台的通用警告。</summary>
    public sealed class ATOWarning : ATOError
    {
        private readonly string _message;

        /// <inheritdoc/>
        public override string TitleKey => "ato.report.title";
        /// <inheritdoc/>
        public override ErrorSeverity Severity => ErrorSeverity.NonFatal;
        /// <inheritdoc/>
        public override string DetailsKey => null;

        /// <summary>EN: Construct with an already-formatted message. ZH: 用已格式化的消息构造。</summary>
        public ATOWarning(string message) { _message = message; }

        /// <inheritdoc/>
        public override string ToMessage() => _message;
        /// <inheritdoc/>
        public override string FormatDetails() => _message;
    }
}
