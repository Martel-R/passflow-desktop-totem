using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PassFlowTotem.Models;

namespace PassFlowTotem.Services
{
    public class KioskDataResponse
    {
        public bool Success { get; set; }
        public OrgInfo? Organization { get; set; }
        public UnitInfo? Unit { get; set; }
        public TicketPrintConfig? TicketPrintConfig { get; set; }
        public List<Category> Categories { get; set; } = new();
        public List<Service> Services { get; set; } = new();
        public List<TicketType> TicketTypes { get; set; } = new();

        public class OrgInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Logo { get; set; } = "";
        }

        public class UnitInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
        }
    }

    public class PairResponse
    {
        public bool Success { get; set; }
        public string Pin { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? OrganizationId { get; set; }
        public string? UnitId { get; set; }
        public List<string>? AllowedServices { get; set; }
        public string? Name { get; set; }
    }

    public class TicketResponse
    {
        public bool Success { get; set; }
        public TicketInfo? Ticket { get; set; }
        public string VirtualQueueUrl { get; set; } = "";

        public class TicketInfo
        {
            public string Id { get; set; } = "";
            public string Number { get; set; } = "";
            public string ServiceId { get; set; } = "";
            public string ServiceName { get; set; } = "";
            public string? SubServiceName { get; set; }
            public string TicketTypeId { get; set; } = "";
            public string TicketTypeName { get; set; } = "";
            public string CreatedAt { get; set; } = "";
            public int QueuePosition { get; set; } = 1;
        }
    }

    public class ApiService
    {
        private readonly HttpClient _http;

        public ApiService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<PairResponse?> CheckOrRegisterPairingAsync(string serverUrl, string pin)
        {
            try
            {
                var cleanServer = serverUrl.TrimEnd('/');
                var url = $"{cleanServer}/api/kiosk/pair";
                var payload = JsonSerializer.Serialize(new { pin, type = "totem" });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var resp = await _http.PostAsync(url, content);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<PairResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao checar pareamento: {ex.Message}");
            }

            return null;
        }

        public async Task<KioskDataResponse?> LoadKioskDataAsync(string serverUrl, string orgId, string unitId)
        {
            try
            {
                var cleanServer = serverUrl.TrimEnd('/');
                var url = $"{cleanServer}/api/kiosk/data?orgId={Uri.EscapeDataString(orgId)}&unitId={Uri.EscapeDataString(unitId)}";

                var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<KioskDataResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao carregar dados do totem: {ex.Message}");
            }

            return null;
        }

        public async Task<GeneratedTicket?> IssueTicketAsync(string serverUrl, string orgId, string unitId, string serviceId, string ticketTypeId, string? subServiceId)
        {
            try
            {
                var cleanServer = serverUrl.TrimEnd('/');
                var url = $"{cleanServer}/api/kiosk/ticket";
                var payload = JsonSerializer.Serialize(new
                {
                    orgId,
                    unitId,
                    serviceId,
                    ticketTypeId,
                    subServiceId,
                    serverUrl = cleanServer
                });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var resp = await _http.PostAsync(url, content);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<TicketResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (data != null && data.Success && data.Ticket != null)
                    {
                        var t = data.Ticket;
                        DateTime.TryParse(t.CreatedAt, out var dt);
                        if (dt == default) dt = DateTime.Now;

                        return new GeneratedTicket
                        {
                            Id = t.Id,
                            Number = t.Number,
                            ServiceId = t.ServiceId,
                            ServiceName = t.ServiceName,
                            SubServiceName = t.SubServiceName,
                            TicketTypeId = t.TicketTypeId,
                            TicketTypeName = t.TicketTypeName,
                            CreatedAt = dt,
                            QueuePosition = t.QueuePosition,
                            VirtualQueueUrl = data.VirtualQueueUrl
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao emitir senha via API: {ex.Message}");
            }

            // Fallback offline (emite senha localmente para nunca travar a fila física!)
            var rndNum = new Random().Next(1, 99);
            var prefix = ticketTypeId.Contains("pref", StringComparison.OrdinalIgnoreCase) ? "P" : "A";
            var fakeNumber = $"{prefix}-{rndNum:D3}";

            return new GeneratedTicket
            {
                Id = Guid.NewGuid().ToString(),
                Number = fakeNumber,
                ServiceId = serviceId,
                ServiceName = "Atendimento Geral",
                TicketTypeId = ticketTypeId,
                TicketTypeName = prefix == "P" ? "Preferencial" : "Convencional",
                CreatedAt = DateTime.Now,
                QueuePosition = 1,
                VirtualQueueUrl = $"{serverUrl}/my-ticket?ticket={fakeNumber}&orgId={orgId}&unitId={unitId}"
            };
        }
    }
}
