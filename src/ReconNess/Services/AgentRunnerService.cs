﻿using ReconNess.Core;
using ReconNess.Core.Models;
using ReconNess.Core.Services;
using ReconNess.Entities;
using ReconNess.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReconNess.Services
{
    /// <summary>
    /// This class implement <see cref="IAgentRunnerService"/>
    /// </summary>
    public class AgentRunnerService : Service<Agent>, IAgentRunnerService, IService<Agent>
    {
        private readonly IAgentService agentService;
        private readonly ITargetService targetService;
        private readonly IRootDomainService rootDomainService;
        private readonly ISubdomainService subdomainService;
        private readonly IAgentRunnerProvider agentRunnerProvider;
        private readonly IAgentBackgroundService agentBackgroundService;

        private readonly INotificationService notificationService;
        private readonly IConnectorService connectorService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AgentRunnerService" /> class
        /// </summary>
        /// <param name="unitOfWork"><see cref="IUnitOfWork"/></param>
        /// <param name="agentService"><see cref="IAgentService"/></param>
        /// <param name="targetService"><see cref="ITargetService"/></param>
        /// <param name="rootDomainService"><see cref="IRootDomainService"/></param>
        /// <param name="subdomainService"><see cref="ISubdomainService"/></param>
        /// <param name="agentRunnerProvider"><see cref="IAgentRunnerProvider"/></param>
        /// <param name="agentBackgroundService"><see cref="IAgentBackgroundService"/></param>
        /// <param name="notificationService"><see cref="INotificationService"/></param>
        /// <param name="connectorService"><see cref="IConnectorService"/></param>
        public AgentRunnerService(IUnitOfWork unitOfWork,
            IAgentService agentService,
            ITargetService targetService,
            IRootDomainService rootDomainService,
            ISubdomainService subdomainService,
            IAgentRunnerProvider agentRunnerProvider,
            IAgentBackgroundService agentBackgroundService,
            INotificationService notificationService,
            IConnectorService connectorService) : base(unitOfWork)
        {
            this.agentService = agentService;
            this.targetService = targetService;
            this.rootDomainService = rootDomainService;
            this.subdomainService = subdomainService;
            this.agentRunnerProvider = agentRunnerProvider;
            this.agentBackgroundService = agentBackgroundService;

            this.notificationService = notificationService;
            this.connectorService = connectorService;
        }

        /// <summary>
        /// <see cref="IAgentRunnerService.RunningAgentsAsync(AgentRunner, CancellationToken)"/>
        /// </summary>
        public async Task<List<string>> RunningAgentsAsync(AgentRunner agentRunner, CancellationToken cancellationToken = default)
        {
            if ((await this.agentRunnerProvider.RunningCountAsync) == 0)
            {
                return new List<string>();
            }

            var agentsRunning = new List<string>();

            var agentRunningkeys = await this.agentRunnerProvider.RunningKeysAsync;

            var agents = await this.agentService.GetAllAsync(cancellationToken);
            foreach (var agent in agents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                agentRunner.Agent = agent;
                var agentKey = AgentRunnerHelpers.GetKey(agentRunner);

                if (agentRunningkeys.Any(c => c.Contains(agentKey)))
                {
                    agentsRunning.Add(agent.Name);
                }
            }

            return agentsRunning;
        }

        /// <summary>
        /// <see cref="IAgentRunnerService.RunAgentAsync(AgentRunner, CancellationToken)"></see>
        /// </summary>
        public async Task RunAgentAsync(AgentRunner agentRunner, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Thread.Sleep(1000);

            var channel = AgentRunnerHelpers.GetChannel(agentRunner);
            await this.agentRunnerProvider.InitializesAsync(channel);

            var subLevelAgentType = this.SubLevelAgentTypeAsync(agentRunner, cancellationToken);
            if (!string.IsNullOrEmpty(subLevelAgentType))
            {
                await this.RunAgenthInEachSubLevelsAsync(agentRunner, channel, subLevelAgentType, cancellationToken);
            }
            else
            {
                var agentKey = AgentRunnerHelpers.GetKey(agentRunner);
                await this.RunAgentAsync(agentKey, agentRunner, channel, last: true);
            }
        }

        /// <summary>
        /// <see cref="IAgentRunnerService.StopAgentAsync(AgentRunner, string, CancellationToken)"></see>
        /// </summary>
        public async Task StopAgentAsync(AgentRunner agentRunner, string channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await this.agentRunnerProvider.StopAsync(channel);
            }
            catch (Exception ex)
            {
                await this.connectorService.SendAsync(channel, ex.Message, true, cancellationToken);
            }
            finally
            {
                await this.SendAgentDoneNotificationAsync(agentRunner, channel, cancellationToken);
            }
        }

        /// <summary>
        /// If we need to run the Agent in each subdomain
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>If we need to run the Agent in each subdomain</returns>
        private string SubLevelAgentTypeAsync(AgentRunner agentRunner, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var types = agentRunner.Agent.AgentTypes.Select(t => t.Type.Name);
            if (types.Contains(AgentTypes.TARGET) && agentRunner.Target == null)
            {
                // we need to run the agent in each target
                return AgentTypes.TARGET;
            }

            if (types.Contains(AgentTypes.ROOTDOMAIN) && agentRunner.RootDomain == null)
            {
                // we need to run the agent in each rootDomain
                return AgentTypes.ROOTDOMAIN;
            }

            if (types.Contains(AgentTypes.SUBDOMAIN) && agentRunner.Subdomain == null)
            {
                // we need to run the agent in each subdomain
                return AgentTypes.SUBDOMAIN;
            }

            return string.Empty;
        }

        /// <summary>
        /// Run bash for each sublevels
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="subLevelAgentType">The sublevel <see cref="AgentTypes"/></param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A Task</returns>
        private async Task RunAgenthInEachSubLevelsAsync(AgentRunner agentRunner, string channel, string subLevelAgentType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AgentTypes.TARGET.Equals(subLevelAgentType))
            {
                await this.RunAgenthInEachTargetsAsync(agentRunner, channel, cancellationToken);
            }
            else if (AgentTypes.ROOTDOMAIN.Equals(subLevelAgentType))
            {
                await this.RunAgentInEachRootDomainsAsync(agentRunner, channel, cancellationToken);
            }
            else if (AgentTypes.SUBDOMAIN.Equals(subLevelAgentType))
            {
                await this.RunAgentInEachSubdomainsAsync(agentRunner, channel, cancellationToken);
            }
        }

        /// <summary>
        /// Run bash for each Target
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A Task</returns>
        private async Task RunAgenthInEachTargetsAsync(AgentRunner agentRunner, string channel, CancellationToken cancellationToken)
        {
            var targets = await this.targetService.GetAllAsync(cancellationToken);
            if (!targets.Any())
            {
                await this.SendAgentDoneNotificationAsync(agentRunner, channel, cancellationToken);
                return;
            }

            var targetsCount = targets.Count;
            foreach (var target in targets)
            {
                var last = targetsCount == 1;
                var newAgentRunner = new AgentRunner
                {
                    Agent = agentRunner.Agent,
                    Target = target,
                    RootDomain = null,
                    Subdomain = null,
                    ActivateNotification = agentRunner.ActivateNotification,
                    Command = agentRunner.Command
                };

                var agentKey = AgentRunnerHelpers.GetKey(newAgentRunner);
                await this.RunAgentAsync(agentKey, newAgentRunner, channel, last);

                targetsCount--;
            }
        }

        /// <summary>
        /// Run bash for each Rootdomain
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A Task</returns>
        private async Task RunAgentInEachRootDomainsAsync(AgentRunner agentRunner, string channel, CancellationToken cancellationToken)
        {
            var rootdomains = await this.rootDomainService.GetAllByCriteriaAsync(r => r.Target == agentRunner.Target, cancellationToken);
            if (!rootdomains.Any())
            {
                await this.SendAgentDoneNotificationAsync(agentRunner, channel, cancellationToken);
                return;
            }

            var rootdomainsCount = rootdomains.Count;
            foreach (var rootdomain in rootdomains)
            {
                var last = rootdomainsCount == 1;
                var newAgentRunner = new AgentRunner
                {
                    Agent = agentRunner.Agent,
                    Target = agentRunner.Target,
                    RootDomain = rootdomain,
                    Subdomain = null,
                    ActivateNotification = agentRunner.ActivateNotification,
                    Command = agentRunner.Command
                };

                var agentKey = AgentRunnerHelpers.GetKey(newAgentRunner);
                await this.RunAgentAsync(agentKey, newAgentRunner, channel, last);

                rootdomainsCount--;
            }
        }

        /// <summary>
        /// Run bash for each Subdomain
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A Task</returns>
        private async Task RunAgentInEachSubdomainsAsync(AgentRunner agentRunner, string channel, CancellationToken cancellationToken)
        {
            var subdomains = await this.subdomainService.GetAllWithIncludesAsync(agentRunner.Target, agentRunner.RootDomain, string.Empty, cancellationToken);
            if (!subdomains.Any())
            {
                await this.SendAgentDoneNotificationAsync(agentRunner, channel, cancellationToken);
                return;
            }

            var subdomainsCount = subdomains.Count;
            foreach (var subdomain in subdomains)
            {
                var last = subdomainsCount == 1;
                var newAgentRunner = new AgentRunner
                {
                    Agent = agentRunner.Agent,
                    Target = agentRunner.Target,
                    RootDomain = agentRunner.RootDomain,
                    Subdomain = subdomain,
                    ActivateNotification = agentRunner.ActivateNotification,
                    Command = agentRunner.Command
                };

                var agentKey = AgentRunnerHelpers.GetKey(newAgentRunner);
                await this.RunAgentAsync(agentKey, newAgentRunner, channel, last);

                subdomainsCount--;
            }
        }

        /// <summary>
        /// Run bash
        /// </summary>
        /// <param name="agentKey">The agent key</param>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to send the menssage</param>
        /// <param name="last">If is the last bash to run</param>
        /// <returns>A task</returns>
        private async Task RunAgentAsync(string agentKey, AgentRunner agentRunner, string channel, bool last)
        {
            if (await this.agentRunnerProvider.IsStoppedAsync(agentKey))
            {
                return;
            }

            var command = AgentRunnerHelpers.GetCommand(agentRunner);
            if (AgentRunnerHelpers.NeedToSkipRun(agentRunner))
            {
                await this.connectorService.SendAsync(channel, $"Skip: {command}");
                await this.IfLastRunStopProcessAsync(agentRunner, channel, last);

                return;
            }

            await this.agentRunnerProvider.RunAsync(new AgentRunnerProviderArgs
            {
                Key = agentKey,
                AgentRunner = agentRunner,
                Channel = channel,
                Command = command,
                Last = last,
                BeginHandlerAsync = BeginHandlerAsync,
                ParserOutputHandlerAsync = ParserOutputHandlerAsync,
                EndHandlerAsync = EndHandlerAsync,
                ExceptionHandlerAsync = ExceptionHandlerAsync
            });
        }

        /// <summary>
        /// <see cref="IAgentRunnerProvider.BeginHandlerAsync"/>
        /// </summary>
        private async Task BeginHandlerAsync(AgentRunnerProviderResult result)
        {
            await this.connectorService.SendAsync(result.Channel, $"RUN: {result.Command}", true, result.CancellationToken);
        }

        /// <summary>
        /// <see cref="IAgentRunnerProvider.ParserOutputHandlerAsync"/>
        /// </summary>
        private async Task ParserOutputHandlerAsync(AgentRunnerProviderResult result)
        {
            await this.connectorService.SendLogsHeadAsync
            (
                result.Channel,
                result.LineCount,
                result.TerminalLineOutput,
                result.ScriptOutput,
                result.CancellationToken
            );

            // Save the Terminal Output Parse 
            await this.agentBackgroundService.SaveOutputParseOnScopeAsync
            (
                result.AgentRunner,
                result.ScriptOutput,
                result.CancellationToken
            );

            await this.connectorService.SendLogsTailAsync(result.Channel, result.LineCount, result.CancellationToken);

            await this.connectorService.SendAsync(result.Channel, result.TerminalLineOutput, false, result.CancellationToken);
        }

        /// <summary>
        /// <see cref="IAgentRunnerProvider.EndHandlerAsync"/>
        /// </summary>
        private async Task EndHandlerAsync(AgentRunnerProviderResult result)
        {
            if (result.AgentRunner.Subdomain != null)
            {
                await this.agentBackgroundService.UpdateSubdomainAgentOnScopeAsync(result.AgentRunner, result.CancellationToken);
            }

            await this.IfLastRunStopProcessAsync
            (
                result.AgentRunner,
                result.Channel,
                result.Last,
                result.CancellationToken
            );
        }

        /// <summary>
        /// <see cref="IAgentRunnerProvider.ExceptionHandlerAsync"/>
        /// </summary>
        private async Task ExceptionHandlerAsync(AgentRunnerProviderResult result)
        {
            await this.connectorService.SendAsync(result.Channel, result.Exception.Message, true, result.CancellationToken);
            await this.connectorService.SendLogsAsync(result.Channel, $"Exception: {result.Exception.StackTrace}", result.CancellationToken);

            await this.IfLastRunStopProcessAsync
            (
                result.AgentRunner,
                result.Channel,
                result.Last,
                result.CancellationToken
            );
        }

        /// <summary>
        /// If is the last bash run we need to stop the Agent
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to use to send the msg</param>
        /// <param name="last">If is the last Agent to run</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task IfLastRunStopProcessAsync(AgentRunner agentRunner, string channel, bool last, CancellationToken cancellationToken = default)
        {
            if (last)
            {
                try
                {
                    await this.StopAgentAsync(agentRunner, channel, cancellationToken);
                    await this.agentBackgroundService.UpdateLastRunAgentOnScopeAsync(agentRunner.Agent, cancellationToken);
                }
                catch (Exception exx)
                {
                    await this.connectorService.SendLogsAsync(channel, $"Exception: {exx.StackTrace}", cancellationToken);
                }
            }
        }


        /// <summary>
        /// Send a msg and a notification when the agent finish
        /// </summary>
        /// <param name="agentRunner">The agent run parameters</param>
        /// <param name="channel">The channel to use to send the msg</param>
        /// <param name="activateNotification">If we need to send a notification</param> 
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task SendAgentDoneNotificationAsync(AgentRunner agentRunner, string channel, CancellationToken cancellationToken)
        {
            if (agentRunner.ActivateNotification)
            {
                await this.notificationService.SendAsync($"Agent {agentRunner.Agent.Name} is done!", cancellationToken);
            }

            await this.connectorService.SendAsync(channel, "Agent done!", false, cancellationToken);
        }
    }
}
