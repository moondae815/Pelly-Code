using System;
using System.Collections.Generic;
using System.Net;
using System.Web.Mvc;
using Shop.Web.Services;

namespace Shop.Web.Controllers
{
    public class OrderController : Controller
    {
        private readonly IOrderService _orderService;

        public OrderController(IOrderService orderService)
        {
            _orderService = orderService;
        }

        public ActionResult Index()
        {
            return View();
        }

        public ActionResult Detail(int id)
        {
            var order = _orderService.GetOrder(id);
            if (order == null)
            {
                return HttpNotFound();
            }

            return View(order);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Cancel(int id, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                TempData["Error"] = "취소 사유를 입력하세요.";
                return RedirectToAction("Detail", new { id = id });
            }

            try
            {
                _orderService.Cancel(id, reason);
                TempData["Message"] = "주문이 취소되었습니다.";
            }
            catch (KeyNotFoundException)
            {
                return HttpNotFound();
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (ArgumentException ex)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest, ex.Message);
            }

            return RedirectToAction("Detail", new { id = id });
        }
    }
}
