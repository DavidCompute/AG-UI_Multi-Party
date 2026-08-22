using System.Globalization;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 数学计算工具：大模型问答中最常用的精确计算（避免模型口算误差）。
/// 手写递归下降解析器 + 白名单字符校验（无 eval / DataTable / 反射），
/// 表达式仅允许数字、运算符、括号、白名单函数与常量，注入无效。
/// 支持 + - * / % ^（右结合幂）、一元正负号、函数（sqrt/abs/round/floor/ceil/min/max/pow/log/ln/exp/sin/cos/tan）、
/// 常量 pi / e、科学计数法（1.5e-3）。
/// </summary>
public static class CalculatorTool
{
    private const int MaxExpressionLength = 200;

    /// <summary>函数名 → 参数个数（-1 表示 ≥1）。</summary>
    private static readonly Dictionary<string, int> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = 1, ["abs"] = 1, ["round"] = 1, ["floor"] = 1, ["ceil"] = 1,
        ["log"] = 1, ["ln"] = 1, ["exp"] = 1, ["sin"] = 1, ["cos"] = 1, ["tan"] = 1,
        ["pow"] = 2, ["min"] = -1, ["max"] = -1,
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
    };

    /// <summary>求值数学表达式，返回结果字符串（非法输入返回错误说明，不抛异常）。</summary>
    public static string Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "计算失败：表达式为空。";
        if (expression.Length > MaxExpressionLength) return $"表达式过长（超过 {MaxExpressionLength} 字符）。";
        try
        {
            var parser = new Parser(expression);
            var value = parser.Parse();
            if (!parser.End()) return $"计算失败：尾部存在无法解析的内容：{parser.Remainder()}";
            if (double.IsNaN(value) || double.IsInfinity(value)) return "结果不是有限数值。";
            return ToolFormat.Number(value);
        }
        catch (Exception ex)
        {
            return "计算失败：" + ex.Message;
        }
    }

    /// <summary>递归下降解析器：expr → term → unary → power → primary。</summary>
    private sealed class Parser(string s)
    {
        private int _pos;

        public bool End() => _pos >= s.Length;

        public string Remainder() => s[_pos..].Trim();

        private void SkipWs()
        {
            while (_pos < s.Length && char.IsWhiteSpace(s[_pos])) _pos++;
        }

        public double Parse()
        {
            SkipWs();
            var v = ParseAddSub();
            SkipWs();
            return v;
        }

        private double ParseAddSub()
        {
            var v = ParseMulDiv();
            while (true)
            {
                SkipWs();
                if (_pos >= s.Length) return v;
                var c = s[_pos];
                if (c == '+') { _pos++; v += ParseMulDiv(); }
                else if (c == '-') { _pos++; v -= ParseMulDiv(); }
                else return v;
            }
        }

        private double ParseMulDiv()
        {
            var v = ParseUnary();
            while (true)
            {
                SkipWs();
                if (_pos >= s.Length) return v;
                var c = s[_pos];
                if (c == '*') { _pos++; v *= ParseUnary(); }
                else if (c == '/')
                {
                    _pos++;
                    var d = ParseUnary();
                    if (d == 0) throw new InvalidOperationException("除数不能为 0");
                    v /= d;
                }
                else if (c == '%')
                {
                    _pos++;
                    var d = ParseUnary();
                    if (d == 0) throw new InvalidOperationException("取模除数不能为 0");
                    v %= d;
                }
                else return v;
            }
        }

        private double ParseUnary()
        {
            SkipWs();
            if (_pos < s.Length && s[_pos] == '-') { _pos++; return -ParseUnary(); }
            if (_pos < s.Length && s[_pos] == '+') { _pos++; return ParseUnary(); }
            return ParsePower();
        }

        private double ParsePower()
        {
            var baseV = ParsePrimary();
            SkipWs();
            if (_pos < s.Length && s[_pos] == '^')
            {
                _pos++;
                var exp = ParseUnary(); // 右结合；一元负号优先于幂（-2^2 = -(2^2)）
                return Math.Pow(baseV, exp);
            }
            return baseV;
        }

        private double ParsePrimary()
        {
            SkipWs();
            if (_pos >= s.Length) throw new InvalidOperationException("表达式不完整");
            var c = s[_pos];
            if (c == '(')
            {
                _pos++;
                var v = ParseAddSub();
                SkipWs();
                if (_pos >= s.Length || s[_pos] != ')') throw new InvalidOperationException("缺少右括号");
                _pos++;
                return v;
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < s.Length && (char.IsDigit(s[_pos]) || s[_pos] == '.'
                    || (s[_pos] is 'e' or 'E')
                    || (s[_pos] is '+' or '-' && _pos > start && s[_pos - 1] is 'e' or 'E')))
                    _pos++;
                var token = s[start.._pos];
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new InvalidOperationException($"无效数字：{token}");
                return num;
            }
            if (char.IsLetter(c))
            {
                var start = _pos;
                while (_pos < s.Length && char.IsLetter(s[_pos])) _pos++;
                var name = s[start.._pos];
                SkipWs();
                if (_pos < s.Length && s[_pos] == '(')
                {
                    _pos++;
                    var args = new List<double>();
                    SkipWs();
                    if (_pos < s.Length && s[_pos] == ')') { _pos++; }
                    else
                    {
                        while (true)
                        {
                            args.Add(ParseAddSub());
                            SkipWs();
                            if (_pos >= s.Length) throw new InvalidOperationException($"函数 {name} 缺少右括号");
                            if (s[_pos] == ',') { _pos++; continue; }
                            if (s[_pos] == ')') { _pos++; break; }
                            throw new InvalidOperationException($"函数 {name} 参数分隔符非法");
                        }
                    }
                    if (!Functions.TryGetValue(name, out var arity)) throw new InvalidOperationException($"未知函数：{name}");
                    if (arity >= 0 && args.Count != arity) throw new InvalidOperationException($"函数 {name} 需要 {arity} 个参数，收到 {args.Count} 个");
                    if (arity < 0 && args.Count == 0) throw new InvalidOperationException($"函数 {name} 至少需要 1 个参数");
                    return name.ToLowerInvariant() switch
                    {
                        "sqrt" => Math.Sqrt(args[0]),
                        "abs" => Math.Abs(args[0]),
                        "round" => Math.Round(args[0], MidpointRounding.AwayFromZero),
                        "floor" => Math.Floor(args[0]),
                        "ceil" => Math.Ceiling(args[0]),
                        "log" => Math.Log10(args[0]),
                        "ln" => Math.Log(args[0]),
                        "exp" => Math.Exp(args[0]),
                        "sin" => Math.Sin(args[0]),
                        "cos" => Math.Cos(args[0]),
                        "tan" => Math.Tan(args[0]),
                        "pow" => Math.Pow(args[0], args[1]),
                        "min" => args.Min(),
                        "max" => args.Max(),
                        _ => throw new InvalidOperationException($"未知函数：{name}"),
                    };
                }
                if (Constants.TryGetValue(name, out var constant)) return constant;
                throw new InvalidOperationException($"未知标识符：{name}");
            }
            throw new InvalidOperationException($"非法字符：{c}");
        }
    }
}
