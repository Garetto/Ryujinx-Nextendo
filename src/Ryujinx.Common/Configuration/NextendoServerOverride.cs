using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Ryujinx.Common.Configuration
{
    /// <summary>
    /// [Nextendo] Serveur personnalisé : faire tourner l'émulateur sur le serveur de
    /// quelqu'un d'autre, entièrement en dehors de Nextendo Network.
    ///
    /// ⚠️ C'EST UN INTERRUPTEUR GÉNÉRAL, PAS UNE SIMPLE REDIRECTION. Quand il est actif,
    /// l'émulateur ne parle plus du tout à Nextendo : pas de compte, pas de jeton, pas
    /// d'amis, pas de sauvegarde en ligne, pas de présence, pas de contrôle de version,
    /// pas de statut Discord Nextendo. C'est <see cref="HorsNextendo"/> qui le décide, et
    /// tout ce qui touche à nos services doit passer par lui.
    ///
    /// Pourquoi si radical : chaque requête vers notre API porte le jeton du compte, et ce
    /// jeton donne un accès complet au compte. Tant que le mode « serveur personnalisé »
    /// laissait le compte actif, il fallait verrouiller l'adresse de l'API pour que
    /// « mets cette IP, les serveurs sont plus rapides » ne devienne pas une méthode de vol
    /// de comptes. En coupant le compte entièrement, le problème disparaît : il n'y a plus
    /// de jeton à détourner. Le mode est plus simple ET plus sûr qu'une redirection
    /// partielle, et il correspond à ce qu'attend vraiment quelqu'un qui joue chez lui.
    ///
    /// The server host may be an IPv4 address or a DNS name. A separate, optional NAT
    /// probe host is only relevant to games that perform peer-to-peer NAT discovery.
    /// </summary>
    public static class NextendoServerOverride
    {
        private sealed class Reglages
        {
            public bool Enabled { get; set; }
            public string ServerHost { get; set; } = "";
            public int ServerPort { get; set; } = DefaultServerPort;
            public string NatHost { get; set; } = "";

            // Read legacy settings written by builds that called these fields IPs.
            public string ServerIp { get; set; } = "";
            public string NatIp { get; set; } = "";
        }

        private const int DefaultServerPort = 443;

        private static string FilePath => Path.Combine(AppDataManager.BaseDirPath, "nextendo_server_override.json");

        private static Reglages _cache;
        private static bool _charge;

        private static Reglages Courant()
        {
            if (_charge)
            {
                return _cache;
            }

            _charge = true;
            _cache = new Reglages();

            try
            {
                if (File.Exists(FilePath))
                {
                    _cache = JsonSerializer.Deserialize<Reglages>(File.ReadAllText(FilePath)) ?? new Reglages();
                }
            }
            catch (Exception ex)
            {
                // Un fichier illisible ne doit pas empêcher l'émulateur de démarrer :
                // on retombe sur les serveurs officiels, ce qui est l'état sûr.
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] override illisible: {ex.Message}");
            }

            return _cache;
        }

        /// <summary>La redirection est-elle active ET utilisable ?</summary>
        public static bool IsActive
        {
            get
            {
                Reglages r = Courant();

                return r.Enabled && ResolveHost(ServerHost(r)) is not null;
            }
        }

        /// <summary>L'adresse à substituer aux serveurs de jeu, ou null si inactive.</summary>
        public static IPAddress ServerAddress => IsActive ? ResolveHost(ServerHost(Courant())) : null;

        /// <summary>
        /// The optional second NAT-check responder. It is not an NEX, Eagle, or game
        /// server endpoint.
        /// </summary>
        public static IPAddress NatAddress => IsActive ? ResolveHost(NatHost(Courant())) : null;

        public static bool Enabled => Courant().Enabled;
        public static string ServerHostText => ServerHost(Courant());
        public static int ServerPort => IsValidPort(Courant().ServerPort) ? Courant().ServerPort : DefaultServerPort;
        public static string NatHostText => NatHost(Courant());

        /// <summary>
        /// Returns a port-rewritten endpoint only for a TCP connection that was DNS redirected
        /// to this custom server. This is the emulator-side equivalent of a server firewall rule
        /// such as 443 -> 20000; unrelated connections and NAT/P2P traffic stay untouched.
        /// </summary>
        public static bool TryGetServerPortRedirect(IPAddress address, int sourcePort, out int destinationPort)
        {
            destinationPort = sourcePort;

            if (!IsActive || sourcePort != DefaultServerPort || ServerPort == sourcePort)
            {
                return false;
            }

            IPAddress serverAddress = ServerAddress;

            if (serverAddress is null || !AddressesEqual(address, serverAddress))
            {
                return false;
            }

            destinationPort = ServerPort;

            return true;
        }

        public static bool IsValidHost(string host)
        {
            string normalized = NormalizeHost(host);

            return normalized.Length != 0 &&
                   (IPAddress.TryParse(normalized, out IPAddress address)
                       ? address.AddressFamily == AddressFamily.InterNetwork
                       : Uri.CheckHostName(normalized) == UriHostNameType.Dns);
        }

        /// <summary>
        /// ⚠️ LA question à poser avant tout ce qui touche à Nextendo. Vraie dès que le mode
        /// « serveur personnalisé » est coché, MÊME si l'adresse saisie est invalide.
        ///
        /// C'est volontaire, et c'est le point délicat : <see cref="IsActive"/> exige une
        /// adresse utilisable, parce qu'on ne peut pas rediriger vers rien. Ici c'est
        /// l'inverse — quelqu'un qui a coché la case a dit qu'il ne veut pas de nos services,
        /// et une faute de frappe dans son adresse ne doit surtout pas le reconnecter en
        /// silence à notre compte et à nos serveurs. Le mode dégradé, c'est « hors ligne »,
        /// pas « retour chez Nextendo ».
        /// </summary>
        public static bool HorsNextendo => Courant().Enabled;

        /// <summary>Enregistre les réglages et les applique au prochain démarrage.</summary>
        public static void Save(bool enabled, string serverHost, int serverPort, string natHost)
        {
            Reglages r = new()
            {
                Enabled = enabled,
                ServerHost = NormalizeHost(serverHost),
                ServerPort = IsValidPort(serverPort) ? serverPort : DefaultServerPort,
                NatHost = NormalizeHost(natHost),
            };

            _cache = r;
            _charge = true;

            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));

                Logger.Info?.Print(LogClass.Application,
                    enabled
                        ? $"[Nextendo] custom server ACTIVE: game -> {r.ServerHost}:{r.ServerPort}, NAT probe -> {(string.IsNullOrEmpty(r.NatHost) ? "(not configured)" : r.NatHost)}"
                        : "[Nextendo] redirection reseau desactivee, retour aux serveurs officiels");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"[Nextendo] override non enregistre: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves a configured host through the host operating system, never the guest DNS
        /// service being intercepted here. Guest DNS only receives the resulting IPv4 address,
        /// so this cannot recurse back into the MITM resolver.
        /// </summary>
        private static IPAddress ResolveHost(string host)
        {
            string normalized = NormalizeHost(host);

            if (normalized.Length == 0)
            {
                return null;
            }

            if (IPAddress.TryParse(normalized, out IPAddress address))
            {
                return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
            }

            if (Uri.CheckHostName(normalized) != UriHostNameType.Dns)
            {
                return null;
            }

            try
            {
                foreach (IPAddress candidate in Dns.GetHostAddresses(normalized))
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return candidate;
                    }
                }
            }
            catch (SocketException)
            {
                // A failed lookup leaves custom-server mode offline rather than falling back
                // to Nextendo, as required by HorsNextendo.
            }

            return null;
        }

        private static string ServerHost(Reglages settings)
        {
            return string.IsNullOrWhiteSpace(settings.ServerHost) ? settings.ServerIp ?? "" : settings.ServerHost;
        }

        private static string NatHost(Reglages settings)
        {
            return string.IsNullOrWhiteSpace(settings.NatHost) ? settings.NatIp ?? "" : settings.NatHost;
        }

        private static string NormalizeHost(string value)
        {
            return (value ?? "").Trim().Trim('"', '\'', '“', '”', '‘', '’');
        }

        private static bool IsValidPort(int port)
        {
            return port is >= 1 and <= 65535;
        }

        private static bool AddressesEqual(IPAddress left, IPAddress right)
        {
            if (left.IsIPv4MappedToIPv6)
            {
                left = left.MapToIPv4();
            }

            if (right.IsIPv4MappedToIPv6)
            {
                right = right.MapToIPv4();
            }

            return left.Equals(right);
        }
    }
}
