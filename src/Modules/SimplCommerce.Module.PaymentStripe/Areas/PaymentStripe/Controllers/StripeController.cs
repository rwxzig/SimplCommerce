﻿using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using SimplCommerce.Infrastructure.Data;
using SimplCommerce.Infrastructure.Helpers;
using SimplCommerce.Module.Checkouts.Services;
using SimplCommerce.Module.Core.Extensions;
using SimplCommerce.Module.Core.Services;
using SimplCommerce.Module.Orders.Models;
using SimplCommerce.Module.Orders.Services;
using SimplCommerce.Module.Payments.Models;
using SimplCommerce.Module.PaymentStripe.Areas.PaymentStripe.ViewModels;
using SimplCommerce.Module.PaymentStripe.Models;
using SimplCommerce.Module.ShoppingCart.Services;
using Stripe;

namespace SimplCommerce.Module.PaymentStripe.Areas.PaymentStripe.Controllers
{
    [Area("PaymentStripe")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class StripeController : Controller
    {
        private readonly ICheckoutService _checkoutService;
        private readonly IOrderService _orderService;
        private readonly IWorkContext _workContext;
        private readonly IRepositoryWithTypedId<PaymentProvider, string> _paymentProviderRepository;
        private readonly IRepository<Payment> _paymentRepository;
        private readonly ICurrencyService _currencyService;
        private readonly IStripeClient _stripeClient;

        public StripeController(
            ICheckoutService checkoutService,
            IOrderService orderService,
            IWorkContext workContext,
            IRepositoryWithTypedId<PaymentProvider, string> paymentProviderRepository,
            IRepository<Payment> paymentRepository,
            ICurrencyService currencyService,
            IStripeClient stripeClient)
        {
            _checkoutService = checkoutService;
            _orderService = orderService;
            _workContext = workContext;
            _paymentProviderRepository = paymentProviderRepository;
            _paymentRepository = paymentRepository;
            _currencyService = currencyService;
            _stripeClient = stripeClient;
        }

		public async Task<IActionResult> Charge(string stripeEmail, string stripeToken, Guid checkoutId)
		{
			var stripeProvider = await _paymentProviderRepository.Query().FirstOrDefaultAsync(x => x.Id == PaymentProviderHelper.StripeProviderId);
			var stripeSetting = JsonConvert.DeserializeObject<StripeConfigForm>(stripeProvider.AdditionalSettings);
			var stripeChargeService = new ChargeService(_stripeClient); // TODO: Fix/Update this.
			var currentUser = await _workContext.GetCurrentUser();

			var cart = await _checkoutService.GetCheckoutDetails(checkoutId);
			if(cart == null)
			{
				return NotFound();
			}

			var orderCreationResult = await _orderService.CreateOrder(checkoutId, "Stripe", 0, OrderStatus.PendingPayment);
			if(!orderCreationResult.Success)
			{
				TempData["Error"] = orderCreationResult.Error;
				return Redirect("~/checkout/payment");
			}

			var order = orderCreationResult.Value;
			var zeroDecimalOrderAmount = order.OrderTotal;
			if(!CurrencyHelper.IsZeroDecimalCurrencies(_currencyService.CurrencyCulture))
			{
				zeroDecimalOrderAmount = zeroDecimalOrderAmount * 100;
			}

			var regionInfo = new RegionInfo(_currencyService.CurrencyCulture.LCID);
			var payment = new Payment()
			{
				OrderId = order.Id,
				Amount = order.OrderTotal,
				PaymentMethod = "Stripe",
				CreatedOn = DateTimeOffset.UtcNow
			};
			try
			{
				var charge = stripeChargeService.Create(new ChargeCreateOptions
				{
					Amount = (int)zeroDecimalOrderAmount,
					Description = "Sample Charge",
					Currency = regionInfo.ISOCurrencySymbol,
					Source = stripeToken
				});

				payment.GatewayTransactionId = charge.Id;
				payment.Status = PaymentStatus.Succeeded;
				order.OrderStatus = OrderStatus.PaymentReceived;
				_paymentRepository.Add(payment);
				await _paymentRepository.SaveChangesAsync();
				return Redirect($"~/checkout/success?orderId={order.Id}");
			}
			catch(StripeException ex)
			{
				payment.Status = PaymentStatus.Failed;
				payment.FailureMessage = ex.StripeError.Message;
				order.OrderStatus = OrderStatus.PaymentFailed;

				_paymentRepository.Add(payment);
				await _paymentRepository.SaveChangesAsync();
				TempData["Error"] = ex.StripeError.Message;
				return Redirect($"~/checkout/error?orderId={order.Id}");
			}
		}
    }
}
