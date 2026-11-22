namespace Gtamp.Shared.Core
{
    /// <summary>
    /// GTA V identifies models, weapons and weather by a Jenkins one-at-a-time hash
    /// of the lower-cased name ("joaat"). Computing it here means configuration and
    /// mod metadata can use readable names while the wire format stays a uint.
    /// </summary>
    public static class GameHash
    {
        public static uint Joaat(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            uint hash = 0;
            foreach (char c in value)
            {
                char lower = c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
                hash += lower;
                hash += hash << 10;
                hash ^= hash >> 6;
            }

            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
            return hash;
        }
    }
}
