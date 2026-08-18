// English: Central logger. All messages start with [ATO].
// 中文：统一日志。所有消息以 [ATO] 开头。
using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    public static class AtoLog
    {
        public static bool Verbose = true;

        public static void Info(string msg)
        {
            Debug.Log("[ATO] " + msg);
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning("[ATO] " + msg);
        }

        public static void Error(string msg)
        {
            Debug.LogError("[ATO] " + msg);
        }

        public static void VerboseInfo(string msg)
        {
            if (Verbose) Debug.Log("[ATO] " + msg);
        }

        public static Scope Time(string label)
        {
            return new Scope(label);
        }

        public struct Scope : IDisposable
        {
            private readonly string _label;
            private readonly Stopwatch _sw;
            public Scope(string label)
            {
                _label = label;
                _sw = Stopwatch.StartNew();
                VerboseInfo(">> " + label);
            }
            public void Dispose()
            {
                _sw.Stop();
                Info(_label + " 耗时/elapsed " + _sw.ElapsedMilliseconds + " ms");
            }
        }
    }
}
