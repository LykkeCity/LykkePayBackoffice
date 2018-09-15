﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutoMapper;
using BackOffice.Areas.LykkePay.Models.Settlement;
using BackOffice.Helpers;
using BackOffice.Models;
using BackOffice.Translates;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Service.BackofficeMembership.Client.Filters;
using Lykke.Service.PayMerchant.Client;
using Lykke.Service.PaySettlement.Client;
using Lykke.Service.PaySettlement.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackOffice.Areas.LykkePay.Controllers
{
    [Authorize]
    [Area("LykkePay")]
    [FilterFeaturesAccess(UserFeatureAccess.LykkePayPaymentRequests)]
    public class SettlementController : Controller
    {
        private readonly IPaySettlementClient _paySettlementClient;
        private readonly IPayMerchantClient _payMerchantClient;
        private readonly ILog _log;

        public SettlementController(IPaySettlementClient paySettlementClient,
            IPayMerchantClient payMerchantClient, ILogFactory logFactory)
        {
            _paySettlementClient = paySettlementClient;
            _payMerchantClient = payMerchantClient;
            _log = logFactory.CreateLog(this);
        }

        [HttpPost]
        public IActionResult Index()
        {
            var viewModel = AreaMenuViewModel.Create(
                AreaMenuItem.Create(Phrases.Log, Url.Action("SettlementCreatedForm")),
                AreaMenuItem.Create(Phrases.Search, Url.Action("SearchForm"))
            );

            return View("ViewAreaMenu", viewModel);
        }

        [HttpPost]
        public IActionResult SettlementCreatedForm()
        {
            return View(new SettlementCreatedFormViewModel
            {
                From = DateTime.Now.AddDays(-1),
                To = DateTime.Now,
                DataUrl = Url.Action("SettlementCreatedData")
            });
        }

        [HttpPost]
        public Task<IActionResult> SettlementCreatedData(SettlementCreatedFormViewModel model)
        {
            return GetPaymentRequestsAsync(() =>
                _paySettlementClient.Api.GetPaymentRequestsBySettlementCreatedAsync(
                    model.From, model.To, model.PageSize, model.ContinuationToken));
        }

        [HttpPost]
        public async Task<IActionResult> SearchForm()
        {
            var model = new SettlementSearchFormViewModel
            {
                Merchants = await _payMerchantClient.Api.GetAllAsync(),
                DataUrl = Url.Action("SearchData")
            };

            model.MerchantId = model.Merchants.FirstOrDefault()?.Id;
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SearchData(SettlementSearchFormViewModel model)
        {
            if (string.IsNullOrEmpty(model.Query))
            {
                return await GetPaymentRequestsAsync(() =>
                    _paySettlementClient.Api.GetPaymentRequestsByMerchantAsync(
                        model.MerchantId, model.PageSize, model.ContinuationToken));
            }

            string query = model.Query.Trim();

            return await GetPaymentRequestsAsync(async () =>
            {
                var tasks = new List<Task<IEnumerable<PaymentRequestModel>>>();
                tasks.Add(GetPaymentRequestsWithoutNotFoundAsync(async () => new[]
                {
                    await _paySettlementClient.Api.GetPaymentRequestAsync(model.MerchantId, query)
                }));
                tasks.Add(GetPaymentRequestsWithoutNotFoundAsync(async () => new[]
                {
                    await _paySettlementClient.Api.GetPaymentRequestByWalletAddressAsync(query)
                }));
                tasks.Add(GetPaymentRequestsWithoutNotFoundAsync(() =>
                    _paySettlementClient.Api.GetPaymentRequestsByTransactionHashAsync(query)
                ));

                await Task.WhenAll(tasks);

                foreach (var task in tasks)
                {
                    if (task.Result?.Any() == true)
                    {
                        return new ContinuationResult<PaymentRequestModel>() {Entities = task.Result};
                    }
                }

                return new ContinuationResult<PaymentRequestModel>();
            });
        }

        private async Task<IActionResult> GetPaymentRequestsAsync(Func<Task<ContinuationResult<PaymentRequestModel>>> func)
        {
            try
            {
                var results = await func();

                if (results?.Entities?.Any() != true)
                {
                    return View("SettlementPaymentRequests", new SettlementPaymentRequestsViewModel
                    {
                        ErrorMessage = Phrases.NoEntities
                    });
                }

                return View("SettlementPaymentRequests", new SettlementPaymentRequestsViewModel
                {
                    PaymentRequests = results.Entities.ToArray(),
                    ContinuationToken = results.ContinuationToken
                });
            }
            catch (Refit.ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return View("SettlementPaymentRequests", new SettlementPaymentRequestsViewModel
                {
                    ErrorMessage = Phrases.NoEntities
                });
            }
            catch (Exception ex)
            {
                _log.Error(ex);
                return View("SettlementPaymentRequests", new SettlementPaymentRequestsViewModel
                {
                    ErrorMessage = Phrases.ApiUnknownError
                });
            }
        }

        private async Task<IEnumerable<PaymentRequestModel>> GetPaymentRequestsWithoutNotFoundAsync(
            Func<Task<IEnumerable<PaymentRequestModel>>> func)
        {
            try
            {
                return await func();
            }
            catch (Refit.ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new PaymentRequestModel[0];
            }
        }
    }
}
