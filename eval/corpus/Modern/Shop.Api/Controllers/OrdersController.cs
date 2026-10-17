using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Shop.Api.Models;
using Shop.Api.Services;

namespace Shop.Api.Controllers
{
    [ApiController]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderService _orderService;

        public OrdersController(IOrderService orderService)
        {
            _orderService = orderService;
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id)
        {
            var orders = await _orderService.SearchAsync(new OrderSearchRequest { PageSize = 1 });
            foreach (var order in orders)
            {
                if (order.OrderId == id)
                {
                    return Ok(order);
                }
            }

            return NotFound();
        }

        [HttpPost("{id:int}/cancel")]
        public async Task<IActionResult> Cancel(int id, [FromBody] CancelRequest body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.Reason))
            {
                return BadRequest("취소 사유가 필요합니다.");
            }

            try
            {
                await _orderService.CancelAsync(id, body.Reason);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<Order>>> Search(
            [FromQuery] int? customerId,
            [FromQuery] OrderStatus? status,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string keyword,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var request = new OrderSearchRequest
            {
                CustomerId = customerId,
                Status = status,
                From = from,
                To = to,
                Keyword = keyword,
                Page = page,
                PageSize = pageSize
            };

            var orders = await _orderService.SearchAsync(request);
            return Ok(orders);
        }
    }
}
