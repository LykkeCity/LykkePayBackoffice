﻿using Autofac;
using AzureRepositories;
using BackOffice.Settings;
using Common.Cache;
using Common.IocContainer;
using Common.Log;
using Lykke.Service.BackofficeMembership.Client;
using Microsoft.Extensions.Configuration;
using Lykke.SettingsReader;
using Lykke.Service.PayInternal.Client;
using Lykke.Service.PayInvoice.Client;
using Lykke.Service.PayAuth.Client;
using QBitNinja.Client;
using Lykke.Service.EmailPartnerRouter.Client;

namespace BackOffice.Binders
{
    public class AzureBinder : IDependencyBinder
    {
        internal static string BlockchainExplorerUrl;
        internal static string PayInvoicePortalResetPasswordLink;
        public ContainerBuilder Bind(IConfigurationRoot configuration, ContainerBuilder builder = null)
        {
            return Bind(configuration, builder, false);
        }

        public ContainerBuilder Bind(IConfigurationRoot configuration, ContainerBuilder builder = null, bool fromTest = false)
        {
            var settings = configuration.LoadSettings<BackOfficeBundle>();
            BlockchainExplorerUrl = settings.CurrentValue.PayBackOffice.BlockchainExplorerUrl;
            PayInvoicePortalResetPasswordLink = settings.CurrentValue.PayBackOffice.PayInvoicePortalResetPasswordLink;
            var ioc = builder ?? new ContainerBuilder();
            ioc.RegisterInstance(settings.CurrentValue.PayBackOffice);
            ioc.RegisterInstance(settings.CurrentValue.PayBackOffice.GoogleAuthSettings);
            ioc.RegisterInstance(settings.CurrentValue.PayBackOffice.TwoFactorVerification ?? new TwoFactorVerificationSettingsEx());
            ioc.RegisterInstance(settings.CurrentValue.PayBackOffice.LykkePayWalletList);

            Log = ioc.BindLog(settings.ConnectionString(x => x.PayBackOffice.Db.LogsConnString), "Backoffice", "LogBackoffce");

            Log.WriteInfoAsync("DResolver", "Binding", "", "Start");

            var cacheManager = new MemoryCacheManager();
            ioc.RegisterInstance<ICacheManager>(cacheManager);

            ioc.RegisterInstance<IEmailPartnerRouterClient>(new EmailPartnerRouterClient(settings.CurrentValue.EmailPartnerRouterServiceClient.ServiceUrl));
            ioc.RegisterInstance<IPayInternalClient>(new PayInternalClient(new PayInternalServiceClientSettings() { ServiceUrl = settings.CurrentValue.PayInternalServiceClient.ServiceUrl }));
            ioc.RegisterInstance<IPayInvoiceClient>(new PayInvoiceClient(new PayInvoiceServiceClientSettings() { ServiceUrl = settings.CurrentValue.PayInvoiceServiceClient.ServiceUrl }));
            ioc.RegisterInstance<IPayAuthClient>(new PayAuthClient(new PayAuthServiceClientSettings() { ServiceUrl = settings.CurrentValue.PayAuthServiceClient.ServiceUrl }, Log));

            ioc.RegisterInstance(new QBitNinjaClient(settings.CurrentValue.NinjaServiceClient.ServiceUrl)).AsSelf();

            ioc.BindBackofficeMembershipClient(settings.CurrentValue.BackofficeMembershipServiceClient.ServiceUrl);

            Log.WriteInfoAsync("DResolver", "Binding", "", "App Stated");

            return ioc;
        }

        public ILog Log { get; set; }
    }
}
