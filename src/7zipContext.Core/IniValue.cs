namespace ZipContext.Core;

/// <summary>所有配置读取器共用的行内注释规则；分号仍可用于后缀列表分隔。</summary>
public static class IniValue
{
    public static string Read(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if ((text[i] is ';' or '#') && (i == 0 || char.IsWhiteSpace(text[i - 1])))
                return text[..i].Trim();
        return text.Trim();
    }
}
