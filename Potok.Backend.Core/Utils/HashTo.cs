using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Potok.Backend.Core.Utils;

public class HashTo
{
    #region md5

    public static string Md5(string intText)
    {
        using (var md5 = MD5.Create())
        {
            var result = md5.ComputeHash(Encoding.UTF8.GetBytes(intText));

            return BitConverter.ToString(result)
                .Replace("-", "")
                .ToLower();
        }
    }

    #endregion

    #region NameToHash

    public static string NameToHash(string nameOrOriginalName, string type)
    {
        return Md5(Regex
                       .Replace(HttpUtility.HtmlDecode(nameOrOriginalName),
                           "[^а-яA-Z0-9]+", "", RegexOptions.IgnoreCase)
                       .ToLower()
                       .Trim()
                   + ":"
                   + type);
    }

    #endregion
}