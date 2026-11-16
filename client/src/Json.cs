using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WbVm
{
    /// <summary>
    /// 极简 JSON 读写。C# 5 环境下不引入任何第三方库，
    /// 也不使用 JavaScriptSerializer（它对 DateTime / 多态节点不友好）。
    /// </summary>
    public sealed class JsonValue
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }

        public Kind Type = Kind.Null;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue = "";
        public List<JsonValue> Items;
        public Dictionary<string, JsonValue> Props;

        public static JsonValue Null() { return new JsonValue(); }

        public static JsonValue NewObject()
        {
            JsonValue v = new JsonValue();
            v.Type = Kind.Object;
            v.Props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            return v;
        }

        public static JsonValue NewArray()
        {
            JsonValue v = new JsonValue();
            v.Type = Kind.Array;
            v.Items = new List<JsonValue>();
            return v;
        }

        public static JsonValue From(string s)
        {
            JsonValue v = new JsonValue();
            v.Type = Kind.String;
            v.StringValue = s == null ? "" : s;
            return v;
        }

        public static JsonValue From(bool b)
        {
            JsonValue v = new JsonValue();
            v.Type = Kind.Bool;
            v.BoolValue = b;
            return v;
        }

        public static JsonValue From(double d)
        {
            JsonValue v = new JsonValue();
            v.Type = Kind.Number;
            v.NumberValue = d;
            return v;
        }

        public bool IsNull { get { return Type == Kind.Null; } }
        public bool IsObject { get { return Type == Kind.Object; } }
        public bool IsArray { get { return Type == Kind.Array; } }

        public string AsString(string fallback)
        {
            if (Type == Kind.String) return StringValue;
            if (Type == Kind.Number) return NumberValue.ToString(CultureInfo.InvariantCulture);
            if (Type == Kind.Bool) return BoolValue ? "true" : "false";
            return fallback;
        }

        public double AsDouble(double fallback)
        {
            if (Type == Kind.Number) return NumberValue;
            if (Type == Kind.String)
            {
                double d;
                if (double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return fallback;
        }

        public int AsInt(int fallback)
        {
            return (int)AsDouble(fallback);
        }

        public bool AsBool(bool fallback)
        {
            if (Type == Kind.Bool) return BoolValue;
            if (Type == Kind.String) return StringValue == "true";
            return fallback;
        }

        public JsonValue Get(string key)
        {
            JsonValue v;
            if (Type == Kind.Object && Props != null && Props.TryGetValue(key, out v)) return v;
            return null;
        }

        public string GetString(string key, string fallback)
        {
            JsonValue v = Get(key);
            return v == null ? fallback : v.AsString(fallback);
        }

        public bool Has(string key)
        {
            return Type == Kind.Object && Props != null && Props.ContainsKey(key);
        }

        public void Set(string key, JsonValue value)
        {
            if (Type != Kind.Object)
            {
                Type = Kind.Object;
                Props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            }
            Props[key] = value;
        }

        public void Set(string key, string value) { Set(key, From(value)); }
        public void Set(string key, bool value) { Set(key, From(value)); }
        public void Set(string key, double value) { Set(key, From(value)); }
        public void Set(string key, int value) { Set(key, From((double)value)); }

        public void Remove(string key)
        {
            if (Type == Kind.Object && Props != null) Props.Remove(key);
        }

        public int Count { get { return Type == Kind.Array ? (Items == null ? 0 : Items.Count) : 0; } }

        public JsonValue Item(int index)
        {
            if (Type != Kind.Array || Items == null || index < 0 || index >= Items.Count) return null;
            return Items[index];
        }

        public void Add(JsonValue value)
        {
            if (Type != Kind.Array)
            {
                Type = Kind.Array;
                Items = new List<JsonValue>();
            }
            Items.Add(value);
        }

        public IEnumerable<JsonValue> Enumerate()
        {
            if (Type == Kind.Array && Items != null) return Items;
            return new List<JsonValue>();
        }

        /* ---------------- 解析 ---------------- */

        public static JsonValue Parse(string text)
        {
            if (text == null) return Null();
            int i = 0;
            JsonValue v = ParseValue(text, ref i);
            return v ?? Null();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return Null();
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return From(ParseString(s, ref i));
            if (c == 't' || c == 'f')
            {
                if (Match(s, i, "true")) { i += 4; return From(true); }
                if (Match(s, i, "false")) { i += 5; return From(false); }
                i++;
                return Null();
            }
            if (c == 'n')
            {
                if (Match(s, i, "null")) i += 4;
                return Null();
            }
            return ParseNumber(s, ref i);
        }

        private static bool Match(string s, int i, string token)
        {
            if (i + token.Length > s.Length) return false;
            return string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            JsonValue obj = NewObject();
            i++; // {
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; }
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                JsonValue val = ParseValue(s, ref i);
                obj.Props[key] = val;
            }
            return obj;
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            JsonValue arr = NewArray();
            i++; // [
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                JsonValue val = ParseValue(s, ref i);
                arr.Items.Add(val);
            }
            return arr;
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') { i++; continue; }
                break;
            }
            string token = s.Substring(start, i - start);
            double d;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return From(d);
            return Null();
        }

        /* ---------------- 序列化 ---------------- */

        public string ToJson(bool pretty)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, bool pretty, int depth)
        {
            switch (Type)
            {
                case Kind.Null: sb.Append("null"); return;
                case Kind.Bool: sb.Append(BoolValue ? "true" : "false"); return;
                case Kind.Number:
                    if (NumberValue == Math.Floor(NumberValue) && Math.Abs(NumberValue) < 1e15)
                        sb.Append(((long)NumberValue).ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append(NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case Kind.String: WriteString(sb, StringValue); return;
                case Kind.Array:
                    {
                        List<JsonValue> items = Items ?? new List<JsonValue>();
                        if (items.Count == 0) { sb.Append("[]"); return; }
                        sb.Append('[');
                        for (int k = 0; k < items.Count; k++)
                        {
                            if (k > 0) sb.Append(',');
                            if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                            items[k].Write(sb, pretty, depth + 1);
                        }
                        if (pretty) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append(']');
                        return;
                    }
                case Kind.Object:
                    {
                        if (Props == null || Props.Count == 0) { sb.Append("{}"); return; }
                        sb.Append('{');
                        bool first = true;
                        foreach (KeyValuePair<string, JsonValue> kv in Props)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                            WriteString(sb, kv.Key);
                            sb.Append(pretty ? ": " : ":");
                            kv.Value.Write(sb, pretty, depth + 1);
                        }
                        if (pretty) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append('}');
                        return;
                    }
            }
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int k = 0; k < depth; k++) sb.Append("  ");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int k = 0; k < s.Length; k++)
                {
                    char c = s[k];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
