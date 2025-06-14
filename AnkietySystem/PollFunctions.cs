using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
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

        public PollFunctions(ApplicationDbContext context, ILogger<PollFunctions> logger)
        {
            _context = context;
            _logger = logger;
        }

        [Function("negotiate")]
        public static SignalRConnectionInfo Negotiate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "negotiate")] HttpRequestData req,
            [SignalRConnectionInfoInput(HubName = "polls")] SignalRConnectionInfo connectionInfo)
        {
            // Zwracamy bezpośrednio obiekt SignalRConnectionInfo, Azure Functions serializuje go do JSON
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