using System.Linq;
using System.Text.RegularExpressions;

namespace HMS.Shared
{
    // Vendored from HmsTeam.UiPath.VS.Projects/Shared/HmsTeam.UiPath.Shared/Core/Functions.cs when
    // SuperTrigger.Web was split into its own repository. The original file mixes in unrelated
    // Orchestrator/UiPath-activity helpers with heavy dependencies (RestSharp, UiPath.Core,
    // CredentialManagement); only the three self-contained string utilities that Exchange.cs actually
    // calls are reproduced here.
    public class Functions
    {
        // If you want to implement both "*" and "?"
        public static string WildCardToRegular(string value)
        {
            return "^" + Regex.Escape(value).Replace("\\?", ".").Replace("\\*", ".*") + "$";
        }

        public static string[] ExtractMailAddressesFromString(string text)
        {
            const string MatchEmailPattern =
              @"(([\w-]+\.)+[\w-]+|([a-zA-Z]{1}|[\w-]{2,}))@"
              + @"((([0-1]?[0-9]{1,2}|25[0-5]|2[0-4][0-9])\.([0-1]?[0-9]{1,2}|25[0-5]|2[0-4][0-9])\."
              + @"([0-1]?[0-9]{1,2}|25[0-5]|2[0-4][0-9])\.([0-1]?[0-9]{1,2}|25[0-5]|2[0-4][0-9])){1}|"
              + @"([a-zA-Z]+[\w-]+\.)+[a-zA-Z]{2,4})";

            Regex rx = new Regex(
              MatchEmailPattern,
              RegexOptions.Compiled | RegexOptions.IgnoreCase);

            MatchCollection matches = rx.Matches(text);

            if (matches != null && matches.Count > 0)
                return matches.Cast<Match>().Select(x => x.Value.ToString()).ToArray();
            else
                return null;
        }

        public static byte[] HexStringToByteArray(string hexString)
        {
            byte[] byteArray = new byte[hexString.Length / 2];
            for (int i = 0; i < hexString.Length; i += 2)
            {
                byteArray[i / 2] = System.Convert.ToByte(hexString.Substring(i, 2), 16);
            }
            return byteArray;
        }
    }
}
