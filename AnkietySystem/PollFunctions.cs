using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
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

        private const string JwtIssuer = "https://ankietyapi-da.azurewebsites.net/";
        private const string JwtAudience = "ankietyapi-users";
        private const string JwtSecurityKey = "super-secret-key-1234-super-long-key!!!";

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
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecurityKey)),
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

        [Function("login")]
        public async Task<HttpResponseData> Login(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "login")] HttpRequestData req)
        {
            string body = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogInformation("Login body: " + body);

            LoginRequest loginReq = null;
            try
            {
                loginReq = JsonSerializer.Deserialize<LoginRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Deserialization error: {ex.Message}");
                var resp = req.CreateResponse(HttpStatusCode.BadRequest);
                await resp.WriteStringAsync("Nieprawidłowy JSON: " + ex.Message);
                return resp;
            }

            _logger.LogInformation($"Parsed login: {loginReq?.Username}, {loginReq?.Password}");

            if (loginReq == null ||
                string.IsNullOrWhiteSpace(loginReq.Username) ||
                string.IsNullOrWhiteSpace(loginReq.Password) ||
                !string.Equals(loginReq.Username, "admin", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(loginReq.Password, "admin123", StringComparison.Ordinal))
            {
                var resp = req.CreateResponse(HttpStatusCode.Unauthorized);
                await resp.WriteStringAsync("Błędny login lub hasło");
                _logger.LogWarning($"Nieudana próba logowania: {loginReq?.Username}, {loginReq?.Password}");
                return resp;
            }

            try
            {
                var key = Encoding.UTF8.GetBytes(JwtSecurityKey);
                var tokenHandler = new JwtSecurityTokenHandler();
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[] {
                        new Claim("sub", loginReq.Username),
                        new Claim(ClaimTypes.Name, loginReq.Username)
                    }),
                    Expires = DateTime.UtcNow.AddHours(2),
                    Issuer = JwtIssuer,
                    Audience = JwtAudience,
                    SigningCredentials = new SigningCredentials(
                        new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var jwt = tokenHandler.WriteToken(token);

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(JsonSerializer.Serialize(new { token = jwt }));
                _logger.LogInformation($"Token wygenerowany dla użytkownika: {loginReq.Username}");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Token generation error: {ex}");
                var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await resp.WriteStringAsync("Błąd generowania tokena: " + ex.Message);
                return resp;
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

    public class LoginRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
}