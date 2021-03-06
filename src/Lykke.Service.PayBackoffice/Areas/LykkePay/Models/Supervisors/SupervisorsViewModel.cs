﻿using Lykke.Service.PayMerchant.Client.Models;
using System.Collections.Generic;

namespace BackOffice.Areas.LykkePay.Models.Supervisors
{
    public class SupervisorsViewModel
    {
    }
    public class SupervisorViewModel
    {
        public string Email { get; set; }
        public string Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public IReadOnlyList<string> SupervisingMerchants { get; set; }
    }
    public class SupervisorsPageViewModel
    {
        public string SelectedMerchant { get; set; }
        public IReadOnlyList<MerchantModel> Merchants { get; set; }
    }
    public class SupervisorsListViewModel
    {
        public string SelectedMerchant { get; set; }
        public IReadOnlyList<SupervisorViewModel> Employees { get; set; }
    }
}
