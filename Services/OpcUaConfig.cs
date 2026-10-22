using System.IO;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace WPF_UartAssist.Services;

/// <summary>
/// OPC UA 的 ApplicationConfiguration 构造。
/// 证书统一放在 %APPDATA%\WPF-UartAssist\pki 下，首次启动会自动生成自签名证书。
/// </summary>
internal static class OpcUaConfig
{
    /// <summary>证书与信任列表的根目录（跟随 settings.json 同一处）。</summary>
    public static string PkiRoot
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WPF-UartAssist",
                "pki");

            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static SecurityConfiguration CreateSecurity(string ownStore, string subjectName)
    {
        string root = PkiRoot;

        return new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(root, ownStore),
                SubjectName = subjectName,
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(root, "trusted"),
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(root, "issuer"),
            },
            RejectedCertificateStore = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(root, "rejected"),
            },

            // 调试工具：一律放行对端证书，否则连本地自建服务端会被拒
            AutoAcceptUntrustedCertificates = true,
            AddAppCertToTrustedStore = true,
        };
    }

    /// <summary>客户端配置。</summary>
    public static ApplicationConfiguration CreateClient(string applicationName)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationUri = $"urn:localhost:{applicationName}",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = CreateSecurity("client", $"CN={applicationName}, DC=localhost"),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration { TraceMasks = 0 },
        };

        config.Validate(ApplicationType.Client).GetAwaiter().GetResult();
        return config;
    }

    /// <summary>服务端配置，只开放 SecurityPolicy=None + 匿名登录，方便本机联调。</summary>
    public static ApplicationConfiguration CreateServer(string applicationName, string endpointUrl)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationUri = $"urn:localhost:{applicationName}",
            ApplicationType = ApplicationType.Server,
            ProductUri = "http://example.org/WPF-UartAssist",
            SecurityConfiguration = CreateSecurity("server", $"CN={applicationName}, DC=localhost"),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { endpointUrl },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new()
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None,
                    },
                },
                UserTokenPolicies = new UserTokenPolicyCollection
                {
                    new(UserTokenType.Anonymous),
                },
                DiagnosticsEnabled = false,
                MaxSessionCount = 50,
                MaxSubscriptionCount = 50,
            },
            TraceConfiguration = new TraceConfiguration { TraceMasks = 0 },
        };

        config.Validate(ApplicationType.Server).GetAwaiter().GetResult();
        return config;
    }
}
