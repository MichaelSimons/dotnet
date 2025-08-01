// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using OSStatus = Interop.AppleCrypto.OSStatus;

namespace System.Net
{
    internal sealed class SafeDeleteSslContext : SafeDeleteContext
    {
        // mapped from OSX error codes
        private const int InitialBufferSize = 2048;
        private readonly SafeSslHandle _sslContext;
        private ArrayBuffer _inputBuffer = new ArrayBuffer(InitialBufferSize);
        private ArrayBuffer _outputBuffer = new ArrayBuffer(InitialBufferSize);

        public SafeSslHandle SslContext => _sslContext;
        public SslApplicationProtocol SelectedApplicationProtocol;
        public bool IsServer;

        public SafeDeleteSslContext(SslAuthenticationOptions sslAuthenticationOptions)
            : base(IntPtr.Zero)
        {
            try
            {
                int osStatus;

                _sslContext = CreateSslContext(sslAuthenticationOptions);

                // Make sure the class instance is associated to the session and is provided
                // in the Read/Write callback connection parameter
                SslSetConnection(_sslContext);

                unsafe
                {
                    osStatus = Interop.AppleCrypto.SslSetIoCallbacks(
                        _sslContext,
                        &ReadFromConnection,
                        &WriteToConnection);
                }

                if (osStatus != 0)
                {
                    throw Interop.AppleCrypto.CreateExceptionForOSStatus(osStatus);
                }

                if (sslAuthenticationOptions.CipherSuitesPolicy != null)
                {
                    uint[] tlsCipherSuites = sslAuthenticationOptions.CipherSuitesPolicy.Pal.TlsCipherSuites;

                    unsafe
                    {
                        fixed (uint* cipherSuites = tlsCipherSuites)
                        {
                            osStatus = Interop.AppleCrypto.SslSetEnabledCipherSuites(
                                _sslContext,
                                cipherSuites,
                                tlsCipherSuites.Length);

                            if (osStatus != 0)
                            {
                                throw Interop.AppleCrypto.CreateExceptionForOSStatus(osStatus);
                            }
                        }
                    }
                }

                if (sslAuthenticationOptions.ApplicationProtocols != null && sslAuthenticationOptions.ApplicationProtocols.Count != 0)
                {
                    if (sslAuthenticationOptions.IsClient)
                    {
                        // On macOS coreTls supports only client side.
                        Interop.AppleCrypto.SslCtxSetAlpnProtos(_sslContext, sslAuthenticationOptions.ApplicationProtocols);
                    }
                    else
                    {
                        // For Server, we do the selection in SslStream and we set it later
                        Interop.AppleCrypto.SslBreakOnClientHello(_sslContext, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write("Exception Caught. - " + ex);
                Dispose();
                throw;
            }

            if (!string.IsNullOrEmpty(sslAuthenticationOptions.TargetHost) && !sslAuthenticationOptions.IsServer && !IPAddress.IsValid(sslAuthenticationOptions.TargetHost))
            {
                Interop.AppleCrypto.SslSetTargetName(_sslContext, sslAuthenticationOptions.TargetHost);
            }

            if (sslAuthenticationOptions.CertificateContext == null && sslAuthenticationOptions.CertSelectionDelegate != null)
            {
                // certificate was not provided but there is user callback. We can break handshake if server asks for certificate
                // and we can try to get it based on remote certificate and trusted issuers.
                Interop.AppleCrypto.SslBreakOnCertRequested(_sslContext, true);
            }

            if (sslAuthenticationOptions.IsServer)
            {
                IsServer = true;

                if (sslAuthenticationOptions.RemoteCertRequired)
                {
                    Interop.AppleCrypto.SslSetAcceptClientCert(_sslContext);
                }

                if (sslAuthenticationOptions.CertificateContext?.Trust?._sendTrustInHandshake == true)
                {
                    SslCertificateTrust trust = sslAuthenticationOptions.CertificateContext!.Trust!;
                    X509Certificate2Collection certList = (trust._trustList ?? trust._store!.Certificates);

                    Debug.Assert(certList != null);
                    Span<IntPtr> handles = certList.Count <= 256
                        ? stackalloc IntPtr[256]
                        : new IntPtr[certList.Count];

                    for (int i = 0; i < certList.Count; i++)
                    {
                        handles[i] = certList[i].Handle;
                    }

                    Interop.AppleCrypto.SslSetCertificateAuthorities(_sslContext, handles.Slice(0, certList.Count), true);
                }
            }
        }

        private static SafeSslHandle CreateSslContext(SslAuthenticationOptions sslAuthenticationOptions)
        {
            switch (sslAuthenticationOptions.EncryptionPolicy)
            {
                case EncryptionPolicy.RequireEncryption:
#pragma warning disable SYSLIB0040 // NoEncryption and AllowNoEncryption are obsolete
                case EncryptionPolicy.AllowNoEncryption:
                    // SecureTransport doesn't allow TLS_NULL_NULL_WITH_NULL, but
                    // since AllowNoEncryption intersect OS-supported isn't nothing,
                    // let it pass.
                    break;
#pragma warning restore SYSLIB0040
                default:
                    throw new PlatformNotSupportedException(SR.Format(SR.net_encryptionpolicy_notsupported, sslAuthenticationOptions.EncryptionPolicy));
            }

            SafeSslHandle sslContext = Interop.AppleCrypto.SslCreateContext(sslAuthenticationOptions.IsServer ? 1 : 0);

            try
            {
                if (sslContext.IsInvalid)
                {
                    // This is as likely as anything.  No error conditions are defined for
                    // the OS function, and our shim only adds a NULL if isServer isn't a normalized bool.
                    throw new OutOfMemoryException();
                }

                // Let None mean "system default"
                if (sslAuthenticationOptions.EnabledSslProtocols != SslProtocols.None)
                {
                    SetProtocols(sslContext, sslAuthenticationOptions.EnabledSslProtocols);
                }

                // SslBreakOnCertRequested does not seem to do anything when we already provide the cert here.
                // So we set it only for server in order to reliably detect whether the peer asked for it on client.
                if (sslAuthenticationOptions.CertificateContext != null && sslAuthenticationOptions.IsServer)
                {
                    SetCertificate(sslContext, sslAuthenticationOptions.CertificateContext);
                }

                Interop.AppleCrypto.SslBreakOnCertRequested(sslContext, true);
                Interop.AppleCrypto.SslBreakOnServerAuth(sslContext, true);
                Interop.AppleCrypto.SslBreakOnClientAuth(sslContext, true);
            }
            catch
            {
                sslContext.Dispose();
                throw;
            }

            return sslContext;
        }

        private void SslSetConnection(SafeSslHandle sslContext)
        {
            GCHandle handle = GCHandle.Alloc(this, GCHandleType.Weak);

            Interop.AppleCrypto.SslSetConnection(sslContext, GCHandle.ToIntPtr(handle));
        }

        public override bool IsInvalid => _sslContext?.IsInvalid ?? true;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SafeSslHandle sslContext = _sslContext;
                if (null != sslContext)
                {
                    lock (_sslContext)
                    {
                        _inputBuffer.Dispose();
                        _outputBuffer.Dispose();
                    }
                    sslContext.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        [UnmanagedCallersOnly]
        private static unsafe int WriteToConnection(IntPtr connection, byte* data, void** dataLength)
        {
            SafeDeleteSslContext? context = (SafeDeleteSslContext?)GCHandle.FromIntPtr(connection).Target;
            Debug.Assert(context != null);

            // We don't pool these buffers and we can't because there's a race between their us in the native
            // read/write callbacks and being disposed when the SafeHandle is disposed. This race is benign currently,
            // but if we were to pool the buffers we would have a potential use-after-free issue.
            try
            {
                lock (context)
                {
                    ulong length = (ulong)*dataLength;
                    Debug.Assert(length <= int.MaxValue);

                    int toWrite = (int)length;
                    var inputBuffer = new ReadOnlySpan<byte>(data, toWrite);

                    context._outputBuffer.EnsureAvailableSpace(toWrite);
                    inputBuffer.CopyTo(context._outputBuffer.AvailableSpan);
                    context._outputBuffer.Commit(toWrite);
                    // Since we can enqueue everything, no need to re-assign *dataLength.

                    return OSStatus.NoErr;
                }
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled())
                    NetEventSource.Error(context, $"WritingToConnection failed: {e.Message}");
                return OSStatus.WritErr;
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe int ReadFromConnection(IntPtr connection, byte* data, void** dataLength)
        {
            SafeDeleteSslContext? context = (SafeDeleteSslContext?)GCHandle.FromIntPtr(connection).Target;
            Debug.Assert(context != null);

            try
            {
                lock (context)
                {
                    ulong toRead = (ulong)*dataLength;

                    if (toRead == 0)
                    {
                        return OSStatus.NoErr;
                    }

                    uint transferred = 0;

                    if (context._inputBuffer.ActiveLength == 0)
                    {
                        *dataLength = (void*)0;
                        return OSStatus.ErrSSLWouldBlock;
                    }

                    int limit = Math.Min((int)toRead, context._inputBuffer.ActiveLength);

                    context._inputBuffer.ActiveSpan.Slice(0, limit).CopyTo(new Span<byte>(data, limit));
                    context._inputBuffer.Discard(limit);
                    transferred = (uint)limit;

                    *dataLength = (void*)transferred;
                    return OSStatus.NoErr;
                }
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled())
                    NetEventSource.Error(context, $"ReadFromConnectionfailed: {e.Message}");
                return OSStatus.ReadErr;
            }
        }

        internal void Write(ReadOnlySpan<byte> buf)
        {
            lock (_sslContext)
            {
                _inputBuffer.EnsureAvailableSpace(buf.Length);
                buf.CopyTo(_inputBuffer.AvailableSpan);
                _inputBuffer.Commit(buf.Length);
            }
        }

        internal int BytesReadyForConnection => _outputBuffer.ActiveLength;

        internal void ReadPendingWrites(ref ProtocolToken token)
        {
            lock (_sslContext)
            {
                if (_outputBuffer.ActiveLength == 0)
                {
                    token.Size = 0;
                    token.Payload = null;

                    return;
                }

                token.SetPayload(_outputBuffer.ActiveSpan);
                _outputBuffer.Discard(_outputBuffer.ActiveLength);
            }
        }

        internal int ReadPendingWrites(byte[] buf, int offset, int count)
        {
            Debug.Assert(buf != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(count >= 0);
            Debug.Assert(count <= buf.Length - offset);

            lock (_sslContext)
            {
                int limit = Math.Min(count, _outputBuffer.ActiveLength);

                _outputBuffer.ActiveSpan.Slice(0, limit).CopyTo(new Span<byte>(buf, offset, limit));
                _outputBuffer.Discard(limit);

                return limit;
            }
        }

        private static readonly SslProtocols[] s_orderedSslProtocols = new SslProtocols[5]
        {
#pragma warning disable 0618
            SslProtocols.Ssl2,
            SslProtocols.Ssl3,
#pragma warning restore 0618
#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1 are obsolete
            SslProtocols.Tls,
            SslProtocols.Tls11,
#pragma warning restore SYSLIB0039
            SslProtocols.Tls12
        };

        private static void SetProtocols(SafeSslHandle sslContext, SslProtocols protocols)
        {
            (int minIndex, int maxIndex) = protocols.ValidateContiguous(s_orderedSslProtocols);
            SslProtocols minProtocolId = s_orderedSslProtocols[minIndex];
            SslProtocols maxProtocolId = s_orderedSslProtocols[maxIndex];

            // Set the min and max.
            Interop.AppleCrypto.SslSetMinProtocolVersion(sslContext, minProtocolId);
            Interop.AppleCrypto.SslSetMaxProtocolVersion(sslContext, maxProtocolId);
        }

        internal static void SetCertificate(SafeSslHandle sslContext, SslStreamCertificateContext context)
        {
            Debug.Assert(sslContext != null);

            IntPtr[] ptrs = new IntPtr[context!.IntermediateCertificates.Count + 1];

            for (int i = 0; i < context.IntermediateCertificates.Count; i++)
            {
                X509Certificate2 intermediateCert = context.IntermediateCertificates[i];

                if (intermediateCert.HasPrivateKey)
                {
                    // In the unlikely event that we get a certificate with a private key from
                    // a chain, clear it to the certificate.
                    //
                    // The current value of intermediateCert is still in elements, which will
                    // get Disposed at the end of this method.  The new value will be
                    // in the intermediate certs array, which also gets serially Disposed.
                    intermediateCert = X509CertificateLoader.LoadCertificate(intermediateCert.RawDataMemory.Span);
                }

                ptrs[i + 1] = intermediateCert.Handle;
            }

            ptrs[0] = context!.TargetCertificate.Handle;

            Interop.AppleCrypto.SslSetCertificate(sslContext, ptrs);
        }
    }
}
