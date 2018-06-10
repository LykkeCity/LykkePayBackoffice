﻿using BackOffice.Helpers;
using BackOffice.Models;
using BackOffice.Services;
using BackOffice.Settings;
using BackOffice.Translates;
using Core.Settings;
using Google.Apis.Auth;
using Lykke.GoogleAuthenticator;
using Lykke.Service.BackofficeMembership.Client;
using Lykke.Service.BackofficeMembership.Client.AutorestClient.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lykke.Service.BackofficeMembership.Client.Models;

namespace BackOffice.Controllers
{
    public class HomeController : Controller
    {
        private readonly IBackofficeMembershipClient _backofficeMembershipClient;
        private readonly GoogleAuthSettings _googleAuthSettings;
        private readonly TwoFactorVerificationSettingsEx _twoFactorVerificationSettings;

        public HomeController(IBackofficeMembershipClient backofficeMembershipClient,
            GoogleAuthSettings googleAuthSettings,
            TwoFactorVerificationSettingsEx twoFactorVerificationSettings)
        {
            _backofficeMembershipClient = backofficeMembershipClient;
            _googleAuthSettings = googleAuthSettings;
            _twoFactorVerificationSettings = twoFactorVerificationSettings;
        }

        public async Task<ActionResult> Index(string langId)
        {


            if (langId != null)
                this.SetLanguage(langId);


            if (User.Identity.IsAuthenticated)
                return View("IndexAuthenticated");

            var sessionId = this.GetSession();

            var viewModel = new IndexPageModel
            {
                BrowserSession = await _backofficeMembershipClient.GetBrowserSessionAsync(sessionId),
                GoogleApiClientId = _googleAuthSettings.ApiClientId
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<ActionResult> Authenticate(AuthenticateModel data)
        {
            try
            {
                var webSignature = await GoogleJsonWebSignatureEx.ValidateAsync(data.GoogleSignInIdToken);

                ActionResult checkError = CheckWebSignature(webSignature);

                if (checkError != null)
                    return checkError;

                var authenticationResult = await _backofficeMembershipClient.AuthenticateAsync(
                    new AuthenticationDataModel
                    {
                        UserId = webSignature.Email,
                        Code = data.Code,
                        Ip = this.GetIp(),
                        SessionId = this.GetSession(),
                        TrustedTimeSpan = TimeSpan.Parse(_twoFactorVerificationSettings.TrustedTimeSpan),
                        UseTwoFactorVerification = _twoFactorVerificationSettings.UseVerification
                    });

                if (authenticationResult.Result == AuthenticationResult.UserNotRegistered
                    || authenticationResult.Result == AuthenticationResult.SecondFactorIsFailed)
                {
                    return this.JsonFailResult(Phrases.UserNotRegistered, "#googleSignIn");
                }

                if (authenticationResult.Result == AuthenticationResult.UserIsDisabled)
                {
                    return this.JsonFailResult(Phrases.UserIsDisabled, "#googleSignIn");
                }

                await SignIn(authenticationResult.User);
            }
            catch (InvalidJwtException)
            {
                return this.JsonFailResult(Phrases.InvalidJwt, "#googleSignIn");
            }

            var divResult = Request.IsMobileBrowser() ? "#pamain" : "body";

            return this.JsonRequestResult(divResult, Url.Action(nameof(BackOfficeController.Layout), "BackOffice"));
        }

        private static ClaimsIdentity MakeIdentity(BackofficeUserModel user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Id),
                new Claim(ClaimTypes.Email, user.Id)
            };

            return new ClaimsIdentity(claims, "Cookie");
        }


        private async Task SignIn(BackofficeUserModel user)
        {
            var identity = MakeIdentity(user);
            await HttpContext.SignInAsync("Cookie", new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
        }

        public ActionResult GoogleSignout()
        {
            return View((object)_googleAuthSettings.ApiClientId);
        }

        public async Task<ActionResult> Logout()
        {
            await HttpContext.SignOutAsync("Cookie");
            return RedirectToAction("Index");
        }

        [HttpGet]
        public ActionResult Version()
        {
            return new JsonResult(new { Version = Microsoft.Extensions.PlatformAbstractions.PlatformServices.Default.Application.ApplicationVersion });
        }

        public ActionResult Result(string message)
        {
            return View("Result", message);
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<ActionResult> CheckTwoFactor(AuthenticateModel data)
        {
            if (!_twoFactorVerificationSettings.UseVerification)
            {
                return Json(new TwoFactorInfo
                {
                    UseVerification = false
                });
            }

            try
            {
                var webSignature = await GoogleJsonWebSignatureEx.ValidateAsync(data.GoogleSignInIdToken);

                var checkError = CheckWebSignature(webSignature);

                if (checkError != null)
                {
                    return checkError;
                }

                string email = webSignature.Email;

                BackofficeUserModel user = UsersCache.GetUsersRolePair(email).User;

                if (user == null)
                {
                    return this.JsonFailResult(Phrases.UserNotRegistered, "#googleSignIn");
                }

                if (user.IsDisabled)
                {
                    return this.JsonFailResult(Phrases.UserIsDisabled, "#googleSignIn");
                }

                if (user.UseTwoFactorVerification)
                {
                    var res = new TwoFactorInfo
                    {
                        ExistCode = user.GoogleAuthenticatorConfirmBinding,
                        UseVerification = !IsTrustedIp(user.TwoFactorVerificationTrustedIPs, this.GetIp())
                    };

                    if (!res.ExistCode)
                    {
                        user = await _backofficeMembershipClient.GenerateGoogleAuthenticatorPrivateKeyAsync(user.Id);

                        var setupInfo =
                            GenerateTwoFactorAuthenticatorSetupCode(user.Id, user.GoogleAuthenticatorPrivateKey);

                        res.ImageUrl = setupInfo.QrCodeSetupImageUrl;
                        res.TextKey = setupInfo.ManualEntryKey;
                    }
                    return Json(res);
                }

                return Json(new TwoFactorInfo
                {
                    UseVerification = false
                });
            }
            catch (InvalidJwtException)
            {
                return this.JsonFailResult(Phrases.InvalidJwt, "#googleSignIn");
            }
        }

        private ActionResult CheckWebSignature(GoogleJsonWebSignatureEx.Payload webSignature)
        {
            if (!string.Equals(webSignature.Audience, _googleAuthSettings.ApiClientId))
            {
                return this.JsonFailResult(Phrases.InvalidGoogleApiClientId, "#googleSignIn");
            }

            if (string.IsNullOrWhiteSpace(webSignature.Email))
            {
                return this.JsonFailResult(Phrases.EmailIsEmpty, "#googleSignIn");
            }

            if (!Regex.IsMatch(webSignature.Email, _googleAuthSettings.AvailableEmailsRegex))
            {
                return this.JsonFailResult(Phrases.EmailShouldBeAtLykke, "#googleSignIn");
            }

            if (!webSignature.IsEmailValidated)
            {
                return this.JsonFailResult(Phrases.EmailIsNotValidated, "#googleSignIn");
            }

            return null;
        }

        private bool CheckTwoFactorAuthenticator(string privateKey, string code)
        {
            var tfa = new TwoFactorAuthenticator();
            var twoFactorResult = tfa.ValidateTwoFactorPIN(privateKey, code);

            return twoFactorResult;
        }

        private SetupCode GenerateTwoFactorAuthenticatorSetupCode(string userId, string privateKey)
        {
            var tfa = new TwoFactorAuthenticator();

            var prefix = !string.IsNullOrEmpty(_twoFactorVerificationSettings.AppNamePrefix)
                ? _twoFactorVerificationSettings.AppNamePrefix + "."
                : "";


            var setupInfo = tfa.GenerateSetupCode(prefix + "LykkeWallet.BackOffice",
                userId, privateKey, 200, 200, true);

            return setupInfo;
        }

        #region Methods for working with user trusted ip addresses

        private static bool IsTrustedIp(string collection, string ip)
        {
            if (string.IsNullOrEmpty(collection))
                return false;

            List<Tuple<string, DateTime>> list = ParceTrustedIpList(collection);

            return !IsExpiredIp(ip, DateTime.UtcNow, list);
        }

        private static string AddTrustedIp(string collection, string ip, TimeSpan trustedTimeSpan)
        {
            List<Tuple<string, DateTime>> list = ParceTrustedIpList(collection);

            DateTime dateTime = DateTime.UtcNow;

            list = list.Where(o => !string.Equals(o.Item1, ip) && !IsExpiredIp(o.Item1, dateTime, list))
                .ToList();

            list.Add(new Tuple<string, DateTime>(ip, dateTime.Add(trustedTimeSpan)));

            return string.Join("&", list.Select(o => $"{o.Item1}|{o.Item2:u}"));
        }

        private static List<Tuple<string, DateTime>> ParceTrustedIpList(string value)
        {
            var result = new List<Tuple<string, DateTime>>();

            if (string.IsNullOrEmpty(value))
                return result;

            foreach (string pair in value.Split("&"))
            {
                string[] values = pair.Split("|");

                if(values.Length != 2)
                    continue;

                if(!DateTime.TryParseExact(values[1], "u", null, DateTimeStyles.AdjustToUniversal, out DateTime experitionDate))
                    continue;

                result.Add(new Tuple<string, DateTime>(values[0], experitionDate));
            }

            return result;
        }

        private static bool IsExpiredIp(string ip, DateTime dateTime, List<Tuple<string, DateTime>> list)
        {
            var trustedIp = list.FirstOrDefault(o => o.Item1 == ip);

            return trustedIp == null || trustedIp.Item2 <= dateTime;
        }

        #endregion
    }
}
