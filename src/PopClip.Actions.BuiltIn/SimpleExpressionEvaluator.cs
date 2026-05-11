using System.Globalization;

namespace PopClip.Actions.BuiltIn;

/// <summary>受限算术表达式求值器：支持 + - * / % 与括号；
/// 故意不依赖 DataTable.Compute / NCalc，避免外部依赖且杜绝注入</summary>
internal static class SimpleExpressionEvaluator
{
    public static double Evaluate(string expression)
    {
        var pos = 0;
        var value = ParseExpr(expression, ref pos);
        SkipWs(expression, ref pos);
        if (pos != expression.Length)
        {
            throw new FormatException($"unexpected trailing input at {pos}");
        }
        return value;
    }

    private static double ParseExpr(string s, ref int pos)
    {
        var left = ParseTerm(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return left;
            var op = s[pos];
            if (op != '+' && op != '-') return left;
            pos++;
            var right = ParseTerm(s, ref pos);
            left = op == '+' ? left + right : left - right;
        }
    }

    private static double ParseTerm(string s, ref int pos)
    {
        var left = ParseFactor(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return left;
            var op = s[pos];
            if (op != '*' && op != '/' && op != '%') return left;
            pos++;
            var right = ParseFactor(s, ref pos);
            left = op switch
            {
                '*' => left * right,
                '/' => right == 0 ? double.NaN : left / right,
                '%' => right == 0 ? double.NaN : left % right,
                _ => left,
            };
        }
    }

    private static double ParseFactor(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos >= s.Length) throw new FormatException("unexpected end");
        var c = s[pos];
        if (c == '+') { pos++; return ParseFactor(s, ref pos); }
        if (c == '-') { pos++; return -ParseFactor(s, ref pos); }
        if (c == '(')
        {
            pos++;
            var v = ParseExpr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') throw new FormatException("missing )");
            pos++;
            return v;
        }
        return ParseNumber(s, ref pos);
    }

    private static double ParseNumber(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.'))
        {
            pos++;
        }
        if (start == pos) throw new FormatException("number expected");
        return double.Parse(s.AsSpan(start, pos - start), CultureInfo.InvariantCulture);
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
