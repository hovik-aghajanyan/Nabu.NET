using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabu.Mcp.AspNetCore;

namespace Nabu.Sample.TodoApi.Controllers
{
    /// <summary>A forecast for one day.</summary>
    public class Forecast
    {
        /// <summary>Date the forecast applies to.</summary>
        public DateOnly Date { get; set; }

        /// <summary>Temperature in degrees Celsius.</summary>
        public int TemperatureC { get; set; }

        /// <summary>Short description of the conditions.</summary>
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Read-only, anonymous endpoints. Placing <c>[McpTool]</c> on the controller exposes every action
    /// on it, which is the shortest possible setup.
    /// </summary>
    [ApiController]
    [Route("api/weather")]
    [AllowAnonymous]
    [McpTool]
    public class WeatherController : ControllerBase
    {
        private static readonly string[] Summaries =
        {
            "Freezing", "Chilly", "Mild", "Warm", "Balmy", "Sweltering",
        };

        /// <summary>Returns a deterministic pseudo-forecast for a city.</summary>
        /// <param name="city">Name of the city to forecast.</param>
        /// <param name="days">How many days to forecast, from 1 to 14.</param>
        [HttpGet("{city}")]
        [McpTool]
        [McpTool(
            "weather_get_yerevan_week",
            Title = "Yerevan week ahead",
            Description = "Returns the seven day forecast for Yerevan. Takes no arguments.",
            ConstantParameters = new[] { "city=Yerevan", "days=7" })]
        public ActionResult<IEnumerable<Forecast>> GetForecast(
            string city,
            [FromQuery] [Range(1, 14)] int days = 3)
        {
            if (string.IsNullOrWhiteSpace(city))
            {
                return BadRequest(new { message = "A city name is required." });
            }

            // Seeded from the city name so the sample and its tests are reproducible.
            var seed = 0;
            foreach (var c in city)
            {
                seed = (seed * 31 + c) % 100000;
            }

            var today = new DateOnly(2026, 1, 1);
            var result = new List<Forecast>(days);
            for (var i = 0; i < days; i++)
            {
                var temperature = ((seed + i * 7) % 45) - 10;
                result.Add(new Forecast
                {
                    Date = today.AddDays(i),
                    TemperatureC = temperature,
                    Summary = Summaries[Math.Abs(temperature + 10) % Summaries.Length],
                });
            }

            return Ok(result);
        }

        /// <summary>Returns the list of cities the sample knows about.</summary>
        [HttpGet("cities")]
        public ActionResult<IEnumerable<string>> GetCities()
        {
            return Ok(new[] { "Yerevan", "Berlin", "Lisbon", "Reykjavik" });
        }
    }
}
