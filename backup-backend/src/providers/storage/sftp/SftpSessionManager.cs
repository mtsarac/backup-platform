using System.Net;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.StorageProviders.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BackupPlatform.StorageProviders.Sftp;

public sealed class SftpSessionManager
{
    private readonly SftpStorageOptions _options;
    private readonly Func<CancellationToken, ValueTask<SftpCredential>> _credentialFactory;
    private readonly SftpNetworkOptions _network;

    public SftpSessionManager(
        SftpStorageOptions options,
        Func<CancellationToken, ValueTask<SftpCredential>> credentialFactory,
        SftpNetworkOptions network)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialFactory = credentialFactory ?? throw new ArgumentNullException(nameof(credentialFactory));
        _network = network ?? throw new ArgumentNullException(nameof(network));
    }

    public async Task<SftpClient> CreateConnectedClientAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pinnedIp = await SftpNetworkValidator.ResolveAndPinAsync(_options.Host, _network, cancellationToken).ConfigureAwait(false);

        var credential = await _credentialFactory(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "No SFTP credentials provided.");
        }

        AuthenticationMethod authMethod;
        if (_options.AuthenticationType.Equals(SftpConfiguration.AuthTypePassword, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(credential.Password))
            {
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Password is required for password authentication.");
            }

            authMethod = new PasswordAuthenticationMethod(_options.Username, credential.Password);
        }
        else if (_options.AuthenticationType.Equals(SftpConfiguration.AuthTypePrivateKey, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(credential.PrivateKey))
            {
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Private key is required for private-key authentication.");
            }

            PrivateKeyFile keyFile;
            try
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credential.PrivateKey));
                keyFile = string.IsNullOrEmpty(credential.Passphrase)
                    ? new PrivateKeyFile(keyStream)
                    : new PrivateKeyFile(keyStream, credential.Passphrase);
            }
            catch (Exception ex)
            {
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Failed to load private key.", ex);
            }

            authMethod = new PrivateKeyAuthenticationMethod(_options.Username, keyFile);
        }
        else
        {
            throw new StorageException(StorageFailureKind.InvalidConfiguration, $"Unsupported authentication type '{_options.AuthenticationType}'.");
        }

        var connectionInfo = new ConnectionInfo(
            pinnedIp.ToString(),
            _options.Port,
            _options.Username,
            authMethod)
        {
            Timeout = _network.ConnectionTimeout,
            Encoding = Encoding.UTF8
        };

        var hostKeyValidated = false;
        var hostKeyRejected = false;
        var client = new SftpClient(connectionInfo)
        {
            OperationTimeout = _network.OperationTimeout,
            BufferSize = 64 * 1024
        };

        client.HostKeyReceived += (_, e) =>
        {
            if (SftpConfiguration.MatchesHostKey(_options.HostKeyFingerprint, e.HostKey))
            {
                e.CanTrust = true;
                hostKeyValidated = true;
            }
            else
            {
                e.CanTrust = false;
                hostKeyValidated = false;
                hostKeyRejected = true;
            }
        };

        try
        {
            using var timeoutCts = new CancellationTokenSource(_network.ConnectionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await client.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            if (!hostKeyValidated)
            {
                client.Disconnect();
                client.Dispose();
                throw new StorageException(StorageFailureKind.HostKeyMismatch, "SFTP host key does not match configured fingerprint.");
            }

            return client;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            client.Dispose();
            throw new StorageException(StorageFailureKind.Timeout, "Connecting to SFTP server timed out.", ex);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "SFTP authentication failed.", ex);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (hostKeyRejected)
            {
                throw new StorageException(StorageFailureKind.HostKeyMismatch, "SFTP host key does not match configured fingerprint.", ex);
            }
            throw SftpExceptionMapper.Map(ex, "Failed to establish SFTP connection.");
        }
    }
}
