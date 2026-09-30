using Mirage;
using UnityEngine;

namespace SkinGate
{
    internal static class NetworkAuthority
    {
        public static bool IsServerActive
        {
            get
            {
                try
                {
                    var server = Object.FindObjectOfType<NetworkServer>();
                    return server != null && server.Active;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
