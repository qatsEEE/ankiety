using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnkietySystem
{
    public class PollFunctions
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<PollFunctions> _logger;
        // W praktyce pobierz te wartości z konfiguracji lub zmiennych środowiskowych!
        private const string JwtIssuer = "https://your-issuer/";
        private const string JwtAudience = "your-audience";
        private const string JwtSecurityKey = "your-256-bit-secret"; // Tylko do testów!

        public PollFunctions(ApplicationDbContext context, ILogger<PollFunctions> logger)
        {
            _context = context;
            _logger = logger;
        }

        // JWT Validation Helper
        private ClaimsPrincipal ValidateToken(HttpRequestData req, out string error)
        {
            error = null;
            if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
            {
                error = "Brak nagłówka Authorization";
                return null;
            }
            var bearer = System.Linq.Enumerable.FirstOrDefault(authHeaders);
            if (string.IsNullOrEmpty(bearer) || !bearer.StartsWith("Bearer "))
            {
                error = "Brak tokena Bearer";
                return null;
            }

            var token = bearer.Substring("Bearer ".Length);
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = JwtIssuer,
                ValidateAudience = true,
                ValidAudience = JwtAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(JwtSecurityKey)),
                ValidateLifetime = true
            };
            try
            {
                var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
                return principal;
            }
            catch (Exception ex)
            {
                error = "Nieprawidłowy token: " + ex.Message;
                return null;
            }
        }

        [Function("negotiate")]
        public static SignalRConnectionInfo Negotiate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "negotiate")] HttpRequestData req,
            [SignalRConnectionInfoInput(HubName = "polls")] SignalRConnectionInfo connectionInfo)
        {
            return connectionInfo;
        }

        [Function("vote")]
        [SignalROutput(HubName = "polls")]
        public async Task<SignalRMessageAction> Vote(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "polls/vote")] HttpRequestData req)
        {
            var user = ValidateToken(req, out var error);
            if (user == null)
            {
                _logger.LogWarning("Unauthorized vote attempt: " + error);
                return null;
            }

            _logger.LogInformation("Processing a vote request.");
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var voteRequest = JsonSerializer.Deserialize<VoteRequest>(requestBody);

            if (voteRequest == null) return null;

            var pollOption = await _context.PollOptions.FindAsync(voteRequest.OptionId);
            if (pollOption == null) return null;

            pollOption.Votes++;
            await _context.SaveChangesAsync();

            return new SignalRMessageAction("newVote")
            {
                Arguments = new object[] { pollOption.PollId, pollOption.Id, pollOption.Votes }
            };
        }

        [Function("GetPoll")]
        public async Task<HttpResponseData> GetPoll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "polls/{id}")] HttpRequestData req, int id)
        {
            var user = ValidateToken(req, out var error);
            if (user == null)
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteStringAsync(error ?? "Brak autoryzacji");
                return unauthorized;
            }

            _logger.LogInformation($"Getting poll with ID: {id}");
            var poll = await _context.Polls
                .Include(p => p.Options)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (poll == null) return req.CreateResponse(HttpStatusCode.NotFound);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(poll);
            return response;
        }

        [Function("CreatePoll")]
        public async Task<HttpResponseData> CreatePoll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "polls")] HttpRequestData req)
        {
            var user = ValidateToken(req, out var error);
            if (user == null)
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteStringAsync(error ?? "Brak autoryzacji");
                return unauthorized;
            }

            _logger.LogInformation("Creating a new poll.");
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var newPoll = JsonSerializer.Deserialize<Poll>(requestBody);

            if (newPoll == null || string.IsNullOrEmpty(newPoll.Question) || newPoll.Options == null || newPoll.Options.Count < 2)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            _context.Polls.Add(newPoll);
            await _context.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(newPoll);
            return response;
        }
    }

    public class VoteRequest
    {
        public int OptionId { get; set; }
    }
}