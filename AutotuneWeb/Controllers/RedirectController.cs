using Microsoft.AspNetCore.Mvc;

namespace AutotuneWeb.Controllers
{
    public class RedirectController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
