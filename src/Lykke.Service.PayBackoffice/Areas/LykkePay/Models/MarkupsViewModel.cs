﻿using BackOffice.Models;
using Lykke.Service.PayInternal.Client.Models.Markup;
using Lykke.Service.PayInternal.Client.Models.Merchant;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BackOffice.Areas.LykkePay.Models
{
    public class MarkupsListViewModel : PagedListModel
    {
        public bool IsFullAccess { get; set; }
        public bool IsEditAccess { get; set; }
        public IEnumerable<MarkupResponse> Markups { get; set; }
        public IEnumerable<MerchantModel> Merchants { get; set; }
        public string SelectedMerchant { get; set; }
    }
}