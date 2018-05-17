﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BackOffice.Filters;
using Core.Users;
using Lykke.Service.PayInternal.Client;
using Lykke.Service.PayInvoice.Client;
using Lykke.Service.PayAuth.Client;
using BackOffice.Controllers;
using BackOffice.Translates;
using BackOffice.Areas.LykkePay.Models;
using QBitNinja.Client;
using QBitNinja.Client.Models;
using NBitcoin;
using MoreLinq;
using Lykke.Service.PayInternal.Client.Models.PaymentRequest;
using Core.Settings;
using Newtonsoft.Json;
using Lykke.Service.PayInternal.Client.Models.Transactions;
using BackOffice.Helpers;
using Lykke.Service.PayInternal.Client.Exceptions;
using Lykke.Common.Api.Contract.Responses;

namespace BackOffice.Areas.LykkePay.Controllers
{
    [Authorize]
    [Area("LykkePay")]
    [FilterFeaturesAccess(UserFeatureAccess.LykkePayTransfersView)]
    public class TransfersController : Controller
    {
        private readonly IPayInternalClient _payInternalClient;
        private readonly IPayInvoiceClient _payInvoiceClient;
        private readonly IPayAuthClient _payAuthClient;
        private readonly QBitNinjaClient _qBitNinjaClient;
        private readonly LykkePayWalletListSettings _walletlist;
        private const int BatchPieceSize = 15;
        private const string ErrorMessageAnchor = "#errorMessage";

        public TransfersController(
            IPayInternalClient payInternalClient, IPayInvoiceClient payInvoiceClient, IPayAuthClient payAuthClient, QBitNinjaClient qBitNinjaClient, LykkePayWalletListSettings walletlist)
        {
            _payInternalClient = payInternalClient;
            _payInvoiceClient = payInvoiceClient;
            _payAuthClient = payAuthClient;
            _qBitNinjaClient = qBitNinjaClient ?? throw new ArgumentNullException(nameof(qBitNinjaClient));
            _walletlist = walletlist;
        }
        public async Task<IActionResult> Index()
        {
            return View();
        }
        [HttpPost]
        public async Task<ActionResult> TransfersPage(string merchant = "")
        {
            var merchants = (await _payInternalClient.GetMerchantsAsync()).ToArray();

            if (!string.IsNullOrEmpty(merchant) && !merchants.Select(x => x.Id).Contains(merchant))
            {
                return this.JsonFailResult(Phrases.InvalidValue, "#merchant");
            }

            if (merchants.Any())
            {
                if (string.IsNullOrEmpty(merchant))
                {
                    merchant = merchants.Select(x => x.Id).First();
                }
            }
            var assetsList = new List<string>();
            assetsList.Add("None");
            return View(new TransfersPageViewModel
            {
                SelectedMerchant = merchant,
                Merchants = merchants,
                Assets = assetsList,
                IsFullAccess = (await this.GetUserRolesPair()).HasAccessToFeature(UserFeatureAccess.LykkePayTransfersFull)
            });
        }
        [HttpPost]
        public async Task<ActionResult> TransfersList(TransfersPageViewModel vm)
        {
            if (string.IsNullOrEmpty(vm.SelectedMerchant))
                return this.JsonFailResult(Phrases.FieldShouldNotBeEmpty, "#selectedMerchant");

            var list = new List<RequestTransferModel>();
            var assetsList = new List<string>();
            var viewModel = new TransfersListViewModel();
            try
            {
                var paymentrequests = await _payInternalClient.GetPaymentRequestsAsync(vm.SelectedMerchant);
                var addresses = paymentrequests.Where(p => (p.Status == PaymentRequestStatus.Confirmed || p.Status == PaymentRequestStatus.Error)
                && p.Amount > 0).ToList();
                var transactions = (await GetTransactions(addresses)).ToList();
                var filtered = transactions.Where(t => t.Amount > 0).ToList();
                assetsList.Add("None");
                foreach (var transaction in filtered)
                {
                    var request = paymentrequests.FirstOrDefault(p => p.WalletAddress == transaction.WalletAddress);
                    var tm = list.FirstOrDefault(r => r.PaymentRequest.WalletAddress == transaction.WalletAddress);
                    if (tm != null)
                    {
                        var amount = (Money)(tm.Amount) + transaction.Amount;
                        tm.Amount = amount.ToString();
                    }
                    else
                    {
                        tm = new RequestTransferModel();
                        tm.Amount = transaction.Amount.ToString();
                        tm.AssetId = transaction.AssetId;
                        tm.PaymentRequest = request;
                        tm.SourceWallet = await _payInternalClient.GetTransactionsSourceWalletsAsync(request.Id);
                        if (vm.SelectedAsset == "None" || tm.AssetId == vm.SelectedAsset)
                            list.Add(tm);

                        if (!assetsList.Contains(transaction.AssetId))
                            assetsList.Add(transaction.AssetId);
                    }
                }
            }
            catch (Exception ex)
            {
                viewModel.Error = ex.InnerException.Message;
            }
            viewModel.List = list;
            viewModel.SelectedMerchant = vm.SelectedMerchant;
            viewModel.Assets = JsonConvert.SerializeObject(assetsList);
            return View(viewModel);
        }
        [HttpPost]
        public async Task<ActionResult> TransferMoneyDialog(TransferModel model)
        {
            var viewmodel = new TransferMoneyDialogViewModel()
            {
                Wallets = _walletlist,
                SelectedMerchant = model.SelectedMerchant,
                SelectedPaymentRequests = model.SelectedPaymentRequests
            };
            return View(viewmodel);
        }
        [HttpPost]
        public async Task<ActionResult> TransferMoney(TransferMoneyDialogViewModel vm)
        {
            var destinationaddress = _walletlist.Wallets.FirstOrDefault(w => w.Address == vm.SelectedWallet);
            if (!string.IsNullOrEmpty(vm.ManualWalletAddress))
                destinationaddress = new LykkePayWalletSettings() { Address = vm.ManualWalletAddress };
            try
            {
                if (vm.SelectedPaymentRequests.Count == 0)
                    return this.JsonFailResult("Error: needs to select payment request", ErrorMessageAnchor);
                var request = new BtcFreeTransferRequest();
                request.DestAddress = destinationaddress.Address;
                var sources = new List<BtcTransferSourceInfo>();
                foreach (var item in vm.SelectedPaymentRequests)
                {
                    var sourceinfo = new BtcTransferSourceInfo();
                    sourceinfo.Address = item.PaymentRequest.WalletAddress;
                    sourceinfo.Amount = Convert.ToDecimal(item.Amount);
                    sources.Add(sourceinfo);
                }
                request.Sources = sources;
                await _payInternalClient.BtcFreeTransferAsync(request);
                return this.JsonRequestResult("#transfersList", Url.Action("TransfersList"), new TransfersPageViewModel() { SelectedMerchant = vm.SelectedMerchant, SelectedAsset = "None" });
            }
            catch (DefaultErrorResponseException ex)
            {
                if (ex.InnerException != null)
                {
                    var content = JsonConvert.DeserializeObject<ErrorResponse>(((Refit.ApiException)ex.InnerException).Content);
                    return this.JsonFailResult(content.ErrorMessage, ErrorMessageAnchor);
                }
                else
                    return this.JsonFailResult(ex.Message, ErrorMessageAnchor);
            }
        }
        [HttpPost]
        public async Task<ActionResult> RefundMoneyDialog(RefundModel model)
        {
            var sources = await _payInternalClient.GetTransactionsSourceWalletsAsync(model.SelectedPaymentRequest);
            var viewmodel = new RefundMoneyDialogViewModel()
            {
                SelectedMerchant = model.SelectedMerchant,
                SelectedPaymentRequest = model.SelectedPaymentRequest,
                SelectedWalletAddress = model.SelectedWalletAddress,
                Wallets = sources
            };
            return View(viewmodel);
        }
        [HttpPost]
        public async Task<ActionResult> RefundMoney(RefundMoneyDialogViewModel vm)
        {
            try
            {
                if (string.IsNullOrEmpty(vm.SelectedWallet))
                    return this.JsonFailResult("Error: needs to select source wallet", ErrorMessageAnchor);
                var refund = new RefundRequestModel()
                {
                    DestinationAddress = vm.ManualWalletAddress ?? vm.SelectedWallet,
                    PaymentRequestId = vm.SelectedPaymentRequest,
                    MerchantId = vm.SelectedMerchant
                };
                var response = await _payInternalClient.RefundAsync(refund);

                return this.JsonRequestResult("#transfersList", Url.Action("TransfersList"), new TransfersPageViewModel() { SelectedMerchant = vm.SelectedMerchant, SelectedAsset = "None"  });
            }
            catch (RefundErrorResponseException ex)
            {
                if (ex.InnerException != null)
                {
                    var content = JsonConvert.DeserializeObject<PayInternalException>(((Refit.ApiException)ex.InnerException).Content);
                    if (content.Code == Helpers.RefundErrorType.Unknown)
                        return this.JsonFailResult("Error code: Internal Error", ErrorMessageAnchor);
                    return this.JsonFailResult("Error code: " + content.Code, ErrorMessageAnchor);
                }
                else
                    return this.JsonFailResult(ex.Message, ErrorMessageAnchor);
            }
        }
        private async Task<IEnumerable<BlockchainTransaction>> GetTransactions(IEnumerable<PaymentRequestModel> addresses)
        {
            var balances = new List<WalletBalanceModel>();

            foreach (var batch in addresses.Batch(BatchPieceSize))
            {
                await Task.WhenAll(batch.Select(address => _qBitNinjaClient.GetBalance(BitcoinAddress.Create(address.WalletAddress))
                    .ContinueWith(t =>
                    {
                        lock (balances)
                        {
                            balances.Add(new WalletBalanceModel
                            {
                                WalletAddress = address.WalletAddress,
                                Balance = t.Result,
                                PaymentAssetId = address.PaymentAssetId
                            });
                        }
                    })));
            }
            return balances.SelectMany(x => x.GetTransactions());
        }

        private class WalletBalanceModel
        {
            public string WalletAddress { get; set; }
            public BalanceModel Balance { get; set; }
            public string PaymentAssetId { get; set; }

            public IEnumerable<BlockchainTransaction> GetTransactions()
            {
                return Balance?.Operations?.Select(x => new BlockchainTransaction
                {
                    WalletAddress = WalletAddress,
                    Amount = x.Amount,
                    AssetId = PaymentAssetId,
                    Blockchain = "Bitcoin",
                    Id = x.TransactionId.ToString(),
                    BlockId = x.BlockId?.ToString(),
                    Confirmations = x.Confirmations
                });
            }
        }
    }
}
