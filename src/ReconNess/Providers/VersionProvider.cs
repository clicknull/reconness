﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReconNess.Core.Providers;
using RestSharp;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReconNess.Providers
{
    /// <summary>
    /// This class implement <see cref="IVersionProvider"/>
    /// </summary>
    public class VersionProvider : IVersionProvider
    {
        private readonly ILogger<VersionProvider> logger;

        private readonly IConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="VersionProvider" /> class
        /// </summary>
        /// <param name="logger"><see cref="ILogger{TCategoryName}"/></param>
        /// <param name="configuration"><see cref="IConfiguration"/></param>
        public VersionProvider(ILogger<VersionProvider> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
        }

        /// <summary>
        /// <see cref="IVersionProvider.GetLatestVersionAsync(CancellationToken)"/>
        /// </summary>
        public async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken)
        {
            var currentVersion = this.configuration["ReconNess:Version"];
            if (!string.IsNullOrEmpty(currentVersion))
            {
                try
                {
                    var client = new RestClient("https://version.reconness.com/");
                    var request = new RestRequest();

                    var response = await client.ExecuteGetAsync(request, cancellationToken);

                    var match = Regex.Match(response.Content, @"<body>\n(.*)\n</body>");
                    if (match.Success && match.Groups.Count == 2)
                    {
                        var latestVersion = match.Groups[1].ToString();
                        int comparison = string.Compare(currentVersion, latestVersion, comparisonType: StringComparison.OrdinalIgnoreCase);
                        if (comparison < 0)
                        {
                            return $"[Latest v{latestVersion}]";
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, ex.Message);
                }
            }

            return string.Empty;
        }
    }
}
