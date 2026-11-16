using System;
using System.Collections.Generic;
using System.IO;

namespace WbVm
{
    /// <summary>客户端自身状态：锁定记录、拦截记录、设置、最后一次安装结果</summary>
    public sealed class StateStore
    {
        private static readonly StateStore _instance = new StateStore();
        public static StateStore Instance { get { return _instance; } }

        private JsonValue _root;

        private StateStore()
        {
            Reload();
        }

        public JsonValue Root
        {
            get
            {
                if (_root == null) Reload();
                return _root;
            }
        }

        public void Reload()
        {
            JsonValue loaded = JsonFile.Read(Paths.StateFile);
            _root = loaded != null && loaded.IsObject ? loaded : JsonValue.NewObject();
            if (!_root.Has("locks")) _root.Set("locks", JsonValue.NewObject());
            if (!_root.Has("blocks")) _root.Set("blocks", JsonValue.NewObject());
            if (!_root.Has("settings")) _root.Set("settings", JsonValue.NewObject());
            JsonValue settings = _root.Get("settings");
            if (!settings.Has("customPaths")) settings.Set("customPaths", JsonValue.NewObject());
        }

        public void Save()
        {
            Paths.EnsureDirs();
            Root.Set("updatedAt", JsonValue.From(DateTime.UtcNow.ToString("o")));
            JsonFile.WriteAtomic(Paths.StateFile, Root);
        }

        public JsonValue Settings { get { return Root.Get("settings"); } }

        public string CustomPath(string appId)
        {
            JsonValue v = Settings.Get("customPaths");
            if (v == null) return "";
            return v.GetString(appId, "");
        }

        public void SetCustomPath(string appId, string path)
        {
            JsonValue v = Settings.Get("customPaths");
            if (v == null)
            {
                v = JsonValue.NewObject();
                Settings.Set("customPaths", v);
            }
            if (string.IsNullOrEmpty(path)) v.Remove(appId);
            else v.Set(appId, path);
            Save();
        }

        /* ---------------- 锁定 ---------------- */

        public JsonValue GetLock(string appId)
        {
            return Root.Get("locks").Get(appId);
        }

        public string LockedVersion(string appId)
        {
            JsonValue l = GetLock(appId);
            return l == null ? "" : l.GetString("version", "");
        }

        public void SetLock(string appId, string version, string note)
        {
            Reload();
            JsonValue locks = Root.Get("locks");
            JsonValue rec = JsonValue.NewObject();
            rec.Set("version", version);
            rec.Set("lockedAt", DateTime.UtcNow.ToString("o"));
            rec.Set("note", note ?? "");
            rec.Set("source", "user");
            locks.Set(appId, rec);
            Save();
        }

        public void ClearLock(string appId)
        {
            Reload();
            Root.Get("locks").Remove(appId);
            Save();
        }

        /* ---------------- 拦截 ---------------- */

        public JsonValue GetBlock(string appId)
        {
            return Root.Get("blocks").Get(appId);
        }

        public void SetBlock(string appId, JsonValue record)
        {
            Reload();
            Root.Get("blocks").Set(appId, record);
            Save();
        }

        public void ClearBlock(string appId)
        {
            Reload();
            Root.Get("blocks").Remove(appId);
            Save();
        }

        public bool AnyBlockEnabled()
        {
            JsonValue blocks = Root.Get("blocks");
            if (blocks == null || blocks.Props == null) return false;
            foreach (KeyValuePair<string, JsonValue> kv in blocks.Props)
            {
                if (kv.Value != null && kv.Value.Get("enabled") != null && kv.Value.Get("enabled").AsBool(false)) return true;
            }
            return false;
        }

        public void SetLastInstall(JsonValue record)
        {
            Reload();
            Root.Set("lastInstall", record);
            Save();
        }
    }
}
