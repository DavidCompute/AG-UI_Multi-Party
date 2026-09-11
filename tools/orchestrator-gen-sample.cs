using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public class Skill
{
    public static string Run(string input)
    {
        try
        {
            string path = (input ?? "").Trim().Trim('"');
            string content;
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                try { content = new UTF8Encoding(false, true).GetString(bytes); }
                catch { content = Encoding.GetEncoding("GB18030").GetString(bytes); }
            }
            else
            {
                content = input ?? "";
            }
            content = content.Replace("\r\n", "\n").Replace('\r', '\n');
            content = Regex.Replace(content, "[\u0000-\u0008\u000B\u000C\u000E-\u001F]", "");
            content = Regex.Replace(content, "[ \t]+", " ");
            content = Regex.Replace(content, "\n{3,}", "\n\n").Trim();
            int chars = content.Length;
            int lines = content.Length == 0 ? 0 : content.Split('\n').Length;
            return "来源: " + (File.Exists(path) ? path : "(直接输入)") + "\n字符数: " + chars + "\n行数: " + lines + "\n----正文----\n" + content;
        }
        catch (Exception ex)
        {
            return "读取失败: " + ex.Message;
        }
    }
}