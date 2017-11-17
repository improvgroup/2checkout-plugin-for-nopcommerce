using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Html;
using Nop.Core.Plugins;
using Nop.Plugin.Payments.TwoCheckout.Controllers;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Orders;
using Nop.Services.Payments;

namespace Nop.Plugin.Payments.TwoCheckout
{
    /// <summary>
    /// 2Checkout payment processor
    /// </summary>
    public class TwoCheckoutPaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly ISettingService _settingService;
        private readonly TwoCheckoutPaymentSettings _twoCheckoutPaymentSettings;
        private readonly IOrderTotalCalculationService _orderTotalCalculationService;
        private readonly ICurrencyService _currencyService;
        private readonly CurrencySettings _currencySettings;
        private readonly ILocalizationService _localizationService;
        private readonly IWebHelper _webHelper;
        private readonly IHttpContextAccessor _httpContextAccessor;

        #endregion

        #region Ctor

        public TwoCheckoutPaymentProcessor(ISettingService settingService, 
            TwoCheckoutPaymentSettings twoCheckoutPaymentSettings,
            IOrderTotalCalculationService orderTotalCalculationService,
            ICurrencyService currencyService,
            CurrencySettings currencySettings,
            ILocalizationService localizationService,
            IWebHelper webHelper,
            IHttpContextAccessor httpContextAccessor)
        {
            this._settingService = settingService;
            this._twoCheckoutPaymentSettings = twoCheckoutPaymentSettings;
            this._orderTotalCalculationService = orderTotalCalculationService;
            this._currencyService = currencyService;
            this._currencySettings = currencySettings;
            this._localizationService = localizationService;
            this._webHelper = webHelper;
            this._httpContextAccessor = httpContextAccessor;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Claculates MD5 hash
        /// </summary>
        /// <param name="input">input</param>
        /// <returns>MD5 hash</returns>
        public string CalculateMD5Hash(string input)
        {
            var md5Hasher = new MD5CryptoServiceProvider();
            var data = md5Hasher.ComputeHash(Encoding.Default.GetBytes(input));
            var sBuilder = new StringBuilder();

            foreach (var t in data)
            {
                sBuilder.Append(t.ToString("x2"));
            }

            return sBuilder.ToString();
        }

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
            return new ProcessPaymentResult { NewPaymentStatus = PaymentStatus.Pending };
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        public void PostProcessPayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            var builder = new StringBuilder();

            var purchaseUrl = _twoCheckoutPaymentSettings.UseSandbox ? "https://sandbox.2checkout.com/checkout/purchase" : "https://www.2checkout.com/checkout/purchase";

            builder.AppendFormat("{0}?id_type=1", purchaseUrl);

            //products
            var orderProducts = postProcessPaymentRequest.Order.OrderItems.ToList();

            for (var i = 0; i < orderProducts.Count; i++)
            {
                var pNum = i + 1;
                var orderItem = orderProducts[i];
                var product = orderProducts[i].Product;

                var cProd = $"c_prod_{pNum}";
                var cProdValue = $"{product.Sku},{orderItem.Quantity}";

                builder.AppendFormat("&{0}={1}", cProd, cProdValue);

                var cName = $"c_name_{pNum}";
                var cNameValue = product.GetLocalized(x => x.Name);

                builder.AppendFormat("&{0}={1}", WebUtility.UrlEncode(cName), WebUtility.UrlEncode(cNameValue));

                var cDescription = $"c_description_{pNum}";
                var cDescriptionValue = product.GetLocalized(x => x.Name);

                if (!string.IsNullOrEmpty(orderItem.AttributeDescription))
                {
                    cDescriptionValue = cDescriptionValue + ". " + orderItem.AttributeDescription;
                    cDescriptionValue = HtmlHelper.StripTags(cDescriptionValue);
                }

                builder.AppendFormat("&{0}={1}", WebUtility.UrlEncode(cDescription), WebUtility.UrlEncode(cDescriptionValue));

                var cPrice = $"c_price_{pNum}";
                var cPriceValue = orderItem.UnitPriceInclTax.ToString("0.00", CultureInfo.InvariantCulture);

                builder.AppendFormat("&{0}={1}", cPrice, cPriceValue);

                var cTangible = $"c_tangible_{pNum}";
                var cTangibleValue = "Y";

                if (product.IsDownload)
                {
                    cTangibleValue = "N";
                }

                builder.AppendFormat("&{0}={1}", cTangible, cTangibleValue);
            }

            builder.AppendFormat("&x_login={0}", _twoCheckoutPaymentSettings.AccountNumber);
            builder.AppendFormat("&sid={0}", _twoCheckoutPaymentSettings.AccountNumber);
            builder.AppendFormat("&x_amount={0}", postProcessPaymentRequest.Order.OrderTotal.ToString("0.00", CultureInfo.InvariantCulture));
            builder.AppendFormat("&currency_code={0}", _currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId).CurrencyCode);
            builder.AppendFormat("&x_invoice_num={0}", postProcessPaymentRequest.Order.Id);
           
            if (_twoCheckoutPaymentSettings.UseSandbox)
                builder.AppendFormat("&demo=Y");

            builder.AppendFormat("&x_First_Name={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.FirstName));
            builder.AppendFormat("&x_Last_Name={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.LastName));
            builder.AppendFormat("&x_Address={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.Address1));
            builder.AppendFormat("&x_City={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.City));

            var billingStateProvince = postProcessPaymentRequest.Order.BillingAddress.StateProvince;

            builder.AppendFormat("&x_State={0}", billingStateProvince != null
                    ? WebUtility.UrlEncode(billingStateProvince.Abbreviation)
                    : WebUtility.UrlEncode(""));

            builder.AppendFormat("&x_Zip={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.ZipPostalCode));

            var billingCountry = postProcessPaymentRequest.Order.BillingAddress.Country;

            builder.AppendFormat("&x_Country={0}", billingCountry != null
                    ? WebUtility.UrlEncode(billingCountry.ThreeLetterIsoCode)
                    : WebUtility.UrlEncode(""));

            builder.AppendFormat("&x_EMail={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.Email));
            builder.AppendFormat("&x_Phone={0}", WebUtility.UrlEncode(postProcessPaymentRequest.Order.BillingAddress.PhoneNumber));
            _httpContextAccessor.HttpContext.Response.Redirect(builder.ToString());
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>true - hide; false - display.</returns>
        public bool HidePaymentMethod(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country

            return false;
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>Additional handling fee</returns>
        public decimal GetAdditionalHandlingFee(IList<ShoppingCartItem> cart)
        {
            var result = this.CalculateAdditionalFee(_orderTotalCalculationService, cart,
                _twoCheckoutPaymentSettings.AdditionalFee, _twoCheckoutPaymentSettings.AdditionalFeePercentage);
            return result;
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();

            result.AddError("Capture method not supported");

            return result;
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public RefundPaymentResult Refund(RefundPaymentRequest refundPaymentRequest)
        {
            var result = new RefundPaymentResult();

            result.AddError("Refund method not supported");

            return result;
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public VoidPaymentResult Void(VoidPaymentRequest voidPaymentRequest)
        {
            var result = new VoidPaymentResult();

            result.AddError("Void method not supported");

            return result;
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();

            result.AddError("Recurring payment not supported");

            return result;
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            var result = new CancelRecurringPaymentResult();

            result.AddError("Recurring payment not supported");

            return result;
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>Result</returns>
        public bool CanRePostProcessPayment(Order order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));

            //do not allow reposting (it can take up to several hours until your order is reviewed
            return false;
        }

        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/PaymentTwoCheckout/Configure";
        }

        public IList<string> ValidatePaymentForm(IFormCollection form)
        {
            var warnings = new List<string>();

            return warnings;
        }

        public ProcessPaymentRequest GetPaymentInfo(IFormCollection form)
        {
            var paymentInfo = new ProcessPaymentRequest();

            return paymentInfo;
        }

        public void GetPublicViewComponent(out string viewComponentName)
        {
            viewComponentName = "PaymentTwoCheckout";
        }

        public Type GetControllerType()
        {
            return typeof(PaymentTwoCheckoutController);
        }

        /// <summary>
        /// Install plugin
        /// </summary>
        public override void Install()
        {
            var settings = new TwoCheckoutPaymentSettings()
            {
                UseSandbox = false,
                AccountNumber = "",
                UseMd5Hashing = true,
                SecretWord = "",
                AdditionalFee = 0,
            };

            _settingService.SaveSetting(settings);

            //locales
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.RedirectionTip", "You will be redirected to 2Checkout site to complete the order.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.UseSandbox", "Use Sandbox");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.UseSandbox.Hint", "Check to enable Sandbox (testing environment).");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AccountNumber", "Account number");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AccountNumber.Hint", "Enter account number.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.UseMd5Hashing", "Use MD5 hashing");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.UseMd5Hashing.Hint", "The MD5 hash is provided to help you verify the authenticity of a sale. This is especially useful for vendors that sell downloadable products, or e - goods, as it can be used to verify whether sale actually came from 2Checkout and was a legitimate live sale.The secret word is set by yourself on the Site Managment page.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.SecretWord", "Secret Word");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.SecretWord.Hint", "Enter secret word.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFee", "Additional fee");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFee.Hint", "Enter additional fee to charge your customers.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFeePercentage", "Additional fee. Use percentage");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFeePercentage.Hint", "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.2Checkout.PaymentMethodDescription", "You will be redirected to 2Checkout site to complete the order.");

            base.Install();
        }

        /// <summary>
        /// Uninstall plugin
        /// </summary>
        public override void Uninstall()
        {
            //locales
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.RedirectionTip");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.UseSandbox");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.UseSandbox.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AccountNumber");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AccountNumber.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.UseMd5Hashing");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.UseMd5Hashing.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.SecretWord");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.SecretWord.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFee");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFee.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFeePercentage");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.AdditionalFeePercentage.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.2Checkout.PaymentMethodDescription");


            base.Uninstall();
        }
        #endregion

        #region Properies

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType
        {
            get
            {
                return RecurringPaymentType.NotSupported;
            }
        }

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType
        {
            get
            {
                return PaymentMethodType.Redirection;
            }
        }

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        public string PaymentMethodDescription
        {
            get { return _localizationService.GetResource("Plugins.Payments.2Checkout.PaymentMethodDescription"); }
        }

        #endregion
    }
}
