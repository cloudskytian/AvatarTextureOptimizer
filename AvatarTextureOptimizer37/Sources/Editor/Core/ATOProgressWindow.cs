// ============================================================================
// ATO - progress / cancel window
// ATO - 进度/取消窗口
//
// Shown while the pipeline runs. Displays the current stage, overall and
// per-stage progress, and a Cancel button. The window is intentionally
// non-modal so the Unity console keeps streaming [ATO] logs in parallel.
// 管线运行期间显示。展示当前阶段、总体/阶段进度与取消按钮。窗口刻意不做模态
// 化，Unity 控制台可并行滚动 [ATO] 日志。
// ============================================================================

#region

using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Core
{
    public class ATOProgressWindow : EditorWindow
    {
        private ATOBuildSession _session;

        [MenuItem("Tools/ATO/Show Progress (隐藏后构建仍继续)")]
        public static void Show()
        {
            var w = GetWindow<ATOProgressWindow>(false, "ATO", true);
            w.minSize = new Vector2(320f, 96f);
        }

        public void Attach(ATOBuildSession session)
        {
            _session = session;
            session.Changed += OnChanged;
            Show();
        }

        public void Detach()
        {
            if (_session != null)
            {
                _session.Changed -= OnChanged;
                _session = null;
            }
        }

        private void OnChanged()
        {
            // EditorApplication.QueuePlayerLoopUpdate is overkill; Repaint on
            // the main thread is enough (events fire on the main thread).
            // 事件都在主线程触发，直接 Repaint 即可。
            Repaint();
        }

        private void OnGUI()
        {
            if (_session == null)
            {
                EditorGUILayout.LabelField("ATO: idle 空闲");
                return;
            }

            EditorGUILayout.Space(4);
            var stage = string.IsNullOrEmpty(_session.StageName)
                ? "..."
                : $"{_session.StageName}  ({_session.StageIndex}/{_session.StageCount})";
            EditorGUILayout.LabelField(stage, EditorStyles.boldLabel);

            float overall = _session.StageCount > 0
                ? (_session.StageIndex + _session.StageProgress) / _session.StageCount
                : 0f;
            EditorGUILayout.ProgressBar(overall);
            EditorGUILayout.ProgressBar(_session.StageProgress);

            EditorGUILayout.Space(4);
            if (!_session.CancelRequested)
            {
                if (GUILayout.Button("Cancel 取消", GUILayout.Height(28)))
                {
                    _session.RequestCancel();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Cancelling… resources will be released and the avatar is left unmodified. " +
                    "正在取消…将释放资源，Avatar 保持未修改。",
                    MessageType.Warning);
            }
        }
    }
}
