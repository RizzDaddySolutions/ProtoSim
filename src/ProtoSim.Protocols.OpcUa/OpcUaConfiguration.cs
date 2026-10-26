using Opc.Ua;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>Builds OPC UA application configurations in code (no XML files needed).</summary>
internal static class OpcUaConfiguration
{
    /// <summary>Certificates live in %LocalAppData%\ProtoSim\pki (own, trusted, issuer, rejected).</summary>
    public static string PkiRoot { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtoSim", "pki");

    private static ITelemetryContext? _telemetry;
    public static ITelemetryContext Telemetry => _telemetry ??= DefaultTelemetry.Create(_ => { });

    public static async Task<ApplicationConfiguration> CreateAsync(
        string applicationName, ApplicationType type, bool autoAccept, Action<ApplicationConfiguration> configure, CancellationToken ct)
    {
        string host = Utils.GetHostName();
        string safeName = new string(applicationName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var config = new ApplicationConfiguration(Telemetry)
        {
            ApplicationName = applicationName,
            ApplicationUri = $"urn:{host}:ProtoSim:{safeName}",
            ProductUri = "urn:ProtoSim",
            ApplicationType = type,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = System.IO.Path.Combine(PkiRoot, "own"),
                    SubjectName = $"CN={applicationName}, O=ProtoSim, DC={host}",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = System.IO.Path.Combine(PkiRoot, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = System.IO.Path.Combine(PkiRoot, "trusted") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = System.IO.Path.Combine(PkiRoot, "rejected") },
                AutoAcceptUntrustedCertificates = autoAccept,
                AddAppCertToTrustedStore = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000, MaxMessageSize = 4 * 1024 * 1024 },
        };
        configure(config);
        await config.ValidateAsync(type, ct);

        if (autoAccept)
        {
            config.CertificateValidator.CertificateValidation += (_, e) =>
            {
                if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
            };
        }
        return config;
    }

    public static NodeId DataTypeId(TagDataType type) => type switch
    {
        TagDataType.Bool => DataTypeIds.Boolean,
        TagDataType.Int16 => DataTypeIds.Int16,
        TagDataType.UInt16 => DataTypeIds.UInt16,
        TagDataType.Int32 => DataTypeIds.Int32,
        TagDataType.UInt32 => DataTypeIds.UInt32,
        TagDataType.Int64 => DataTypeIds.Int64,
        TagDataType.UInt64 => DataTypeIds.UInt64,
        TagDataType.Float32 => DataTypeIds.Float,
        TagDataType.Float64 => DataTypeIds.Double,
        _ => DataTypeIds.String,
    };

    /// <summary>Maps an OPC UA built-in type to the closest tag type (null when not a simple scalar).</summary>
    public static TagDataType? TagType(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => TagDataType.Bool,
        BuiltInType.SByte or BuiltInType.Int16 => TagDataType.Int16,
        BuiltInType.Byte or BuiltInType.UInt16 => TagDataType.UInt16,
        BuiltInType.Int32 => TagDataType.Int32,
        BuiltInType.UInt32 => TagDataType.UInt32,
        BuiltInType.Int64 => TagDataType.Int64,
        BuiltInType.UInt64 => TagDataType.UInt64,
        BuiltInType.Float => TagDataType.Float32,
        BuiltInType.Double => TagDataType.Float64,
        BuiltInType.String => TagDataType.String,
        _ => null,
    };
}
