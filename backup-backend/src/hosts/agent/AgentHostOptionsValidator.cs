using Microsoft.Extensions.Options;

namespace BackupPlatform.Agent.Host;

/// <summary>Fails startup with one message per invalid setting.</summary>
public sealed class AgentHostOptionsValidator : IValidateOptions<AgentHostOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var prefix = AgentHostOptions.SectionName + ":";

        var baseUri = options.Backend?.BaseUri;
        if (string.IsNullOrWhiteSpace(baseUri))
        {
            failures.Add($"{prefix}Backend:BaseUri is required: set it to the absolute https address of the backend.");
        }
        else if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{prefix}Backend:BaseUri must be an absolute https URI; credentials are never sent over plain HTTP.");
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add($"{prefix}Backend:BaseUri must not contain user information.");
        }

        if (options.CredentialStoreDirectory is { } directory
            && (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)))
        {
            failures.Add($"{prefix}CredentialStoreDirectory must be an absolute path when set.");
        }

        if (options.Connectivity is null)
        {
            failures.Add($"{prefix}Connectivity is missing.");
        }
        else
        {
            failures.AddRange(options.Connectivity.Validate().Select(problem => $"{prefix}Connectivity:{problem}"));
        }

        failures.AddRange(AgentBackupOptionsValidation.Validate(options.Backup));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
