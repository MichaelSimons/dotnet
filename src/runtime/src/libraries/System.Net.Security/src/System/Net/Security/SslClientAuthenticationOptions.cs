// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Security
{
    public class SslClientAuthenticationOptions
    {
        private EncryptionPolicy _encryptionPolicy = EncryptionPolicy.RequireEncryption;
        private X509RevocationMode _checkCertificateRevocation = SslAuthenticationOptions.DefaultRevocationMode;
        private SslProtocols _enabledSslProtocols = SslProtocols.None;
        private bool _allowRenegotiation = true;
        private bool _allowTlsResume = true;

        public bool AllowRenegotiation
        {
            get => _allowRenegotiation;
            set => _allowRenegotiation = value;
        }

        /// <summary>
        ///  Gets or sets a value that indicates whether the SslStream should allow TLS resumption.
        /// </summary>
        public bool AllowTlsResume
        {
            get => _allowTlsResume;
            set => _allowTlsResume = value;
        }

        public LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get; set; }

        public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        public List<SslApplicationProtocol>? ApplicationProtocols { get; set; }

        public string? TargetHost { get; set; }

        public X509CertificateCollection? ClientCertificates { get; set; }

        /// <summary>
        /// Gets or sets the client certificate context.
        /// </summary>
        public SslStreamCertificateContext? ClientCertificateContext { get; set; }

        public X509RevocationMode CertificateRevocationCheckMode
        {
            get => _checkCertificateRevocation;
            set
            {
                if (value != X509RevocationMode.NoCheck && value != X509RevocationMode.Offline && value != X509RevocationMode.Online)
                {
                    throw new ArgumentException(SR.Format(SR.net_invalid_enum, nameof(X509RevocationMode)), nameof(value));
                }

                _checkCertificateRevocation = value;
            }
        }

        public EncryptionPolicy EncryptionPolicy
        {
            get => _encryptionPolicy;
            set
            {
#pragma warning disable SYSLIB0040 // NoEncryption and AllowNoEncryption are obsolete
                if (value != EncryptionPolicy.RequireEncryption && value != EncryptionPolicy.AllowNoEncryption && value != EncryptionPolicy.NoEncryption)
                {
                    throw new ArgumentException(SR.Format(SR.net_invalid_enum, nameof(EncryptionPolicy)), nameof(value));
                }
#pragma warning restore SYSLIB0040

                _encryptionPolicy = value;
            }
        }

        public SslProtocols EnabledSslProtocols
        {
            get => _enabledSslProtocols;
            set => _enabledSslProtocols = value;
        }

        /// <summary>
        /// Specifies cipher suites allowed to be used for TLS.
        /// When set to null operating system default will be used.
        /// Use extreme caution when changing this setting.
        /// </summary>
        public CipherSuitesPolicy? CipherSuitesPolicy { get; set; }

        /// <summary>
        /// Gets or sets an optional customized policy for remote certificate
        /// validation. If not <see langword="null"/>,
        /// <see cref="CertificateRevocationCheckMode"/> and <see cref="SslCertificateTrust"/>
        /// are ignored.
        /// </summary>
        public X509ChainPolicy? CertificateChainPolicy { get; set; }

        private bool _allowRsaPssPadding = true;
        /// <summary>
        /// Gets or sets a value that indicates whether the the rsa_pss_* family of TLS signature algorithms is enabled for use in the TLS handshake.
        /// </summary>
        public bool AllowRsaPssPadding
        {
            get => _allowRsaPssPadding;

            [SupportedOSPlatform("windows")]
            [SupportedOSPlatform("linux")]
            set { _allowRsaPssPadding = value; }
        }

        private bool _allowRsaPkcs1Padding = true;
        /// <summary>
        /// Gets or sets a value that indicates whether the the rsa_pkcs1_* family of TLS signature algorithms is enabled for use in the TLS handshake.
        /// </summary>
        public bool AllowRsaPkcs1Padding
        {
            get => _allowRsaPkcs1Padding;

            [SupportedOSPlatform("windows")]
            [SupportedOSPlatform("linux")]
            set { _allowRsaPkcs1Padding = value; }
        }
    }
}
