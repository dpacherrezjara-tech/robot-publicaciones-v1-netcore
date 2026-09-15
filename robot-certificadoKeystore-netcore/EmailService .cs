using CertificadoAutomatico;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CertificadoAutomatico;

/// <summary>
/// Cliente del Puente de Correo (mi-services) — POST /api/v1/email
/// Reemplaza al SMTP básico con contraseña compartida.
/// Misma firma pública que la versión SMTP anterior.
/// </summary>
public class EmailService
{
    private readonly string _url;
    private readonly string _token;
    private readonly string _to;
    private readonly string _cc;
    private readonly string _subject;
    private readonly string _environmentName;
    private readonly bool _enabled;

    // HttpClient compartido (recomendado: uno por proceso)
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public EmailService(Dictionary<string, string> config)
    {
        string enable = ObtenerValorConfig(config, "ENABLE_EMAIL", "EMAIL_SETTINGS") ?? "false";
        _enabled = enable.ToLower() == "true";

        if (!_enabled)
            return;

        _url = ObtenerValorConfig(config, "EMAIL_URL", "EMAIL_SETTINGS");
        _token = ObtenerValorConfig(config, "EMAIL_TOKEN", "EMAIL_SETTINGS");
        _to = ObtenerValorConfig(config, "EMAIL_TO", "EMAIL_SETTINGS");
        _cc = ObtenerValorConfig(config, "EMAIL_CC", "EMAIL_SETTINGS");

        string subjectTemplate = ObtenerValorConfig(config, "EMAIL_SUBJECT", "EMAIL_SETTINGS")
                                 ?? "Actualización Certificados SSL - [ENTORNO]";

        _environmentName = ObtenerValorConfig(config, "ENV_NAME", "ENVIRONMENT_SETTINGS") ?? "Sin Entorno";
        _subject = subjectTemplate.Replace("[ENTORNO]", _environmentName);
    }

    /// <summary>
    /// Envía la notificación. Misma firma que la versión SMTP.
    /// Devuelve true si el puente aceptó el correo (202).
    /// </summary>
    public bool SendSuccessNotification(
        string expiryDate,
        string serverName,
        string estado,
        string colorEstado,
        out string error)
    {
        error = "";

        if (!_enabled)
        {
            Program.EscribirLog("[EMAIL] Notificaciones por correo desactivadas.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(_url))
        {
            error = "No se especificó EMAIL_URL en [EMAIL_SETTINGS]";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_token))
        {
            error = "No se especificó EMAIL_TOKEN en [EMAIL_SETTINGS]";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_to))
        {
            error = "No se especificó destinatario (EMAIL_TO)";
            return false;
        }

        try
        {
            string fechaActual = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            string fechaExpiracion = string.IsNullOrEmpty(expiryDate) ? "No disponible" : expiryDate;
            string servidor = string.IsNullOrEmpty(serverName) ? Environment.MachineName : serverName;

            string body = ConstruirCuerpoHtml(_environmentName, servidor, fechaActual, estado, colorEstado, fechaExpiracion);

            var payload = new EmailRequest
            {
                To = _to,
                Subject = _subject,
                Body = body,
                Cc = string.IsNullOrWhiteSpace(_cc) ? null : _cc
            };

            var resultado = EnviarConReintentos(payload, intentos: 3).GetAwaiter().GetResult();

            if (resultado.Exito)
            {
                Program.EscribirLog($"[EMAIL] Notificación enviada a: {_to}");
                return true;
            }

            error = resultado.Error;
            return false;
        }
        catch (Exception ex)
        {
            error = $"Error al enviar correo: {ex.Message}";
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Internos
    // ─────────────────────────────────────────────────────────────

    private static string ConstruirCuerpoHtml(
        string environmentName,
        string servidor,
        string fechaActual,
        string estado,
        string colorEstado,
        string fechaExpiracion)
    {
        // Todo el CSS va inline (los clientes de correo ignoran <style> y clases)
        return $@"
            <div style='background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 20px; font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
                <h3 style='margin-top: 0; color: #2b6cb0; border-bottom: 1px solid #edf2f7; padding-bottom: 10px;'>
                    Reporte de Actualización SSL - {Escapar(environmentName)}
                </h3>
                
                <table style='width: 100%; font-size: 14px; color: #2d3748; border-collapse: collapse;'>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Servidor / SSL:</td>
                        <td style='padding: 8px 0;'>{Escapar(servidor)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Fecha de Ejecución:</td>
                        <td style='padding: 8px 0;'>{fechaActual}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Estado:</td>
                        <td style='padding: 8px 0; color: {colorEstado}; font-weight: bold;'>{Escapar(estado)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Nueva Caducidad:</td>
                        <td style='padding: 8px 0; color: #e53e3e; font-weight: bold;'>{Escapar(fechaExpiracion)}</td>
                    </tr>
                </table>

                <div style='margin-top: 20px; padding-top: 10px; border-top: 1px solid #edf2f7; font-size: 12px; color: #718096; text-align: center;'>
                    Este es un mensaje automático generado por el sistema de actualización de certificados.<br />
                    Por favor, no responder a este correo.
                </div>
            </div>";
    }

    private static string Escapar(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    private class ResultadoEnvio
    {
        public bool Exito { get; set; }
        public string Error { get; set; }
    }

    private async Task<ResultadoEnvio> EnviarConReintentos(EmailRequest payload, int intentos)
    {
        string json = JsonSerializer.Serialize(payload, JsonOpts);

        for (int intento = 0; intento < intentos; intento++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var res = await _http.SendAsync(req);

                // Acepta cualquier 2xx (el puente devuelve 202)
                if (res.IsSuccessStatusCode)
                {
                    return new ResultadoEnvio { Exito = true };
                }

                // 429 → reintentar respetando Retry-After
                if ((int)res.StatusCode == 429 && intento < intentos - 1)
                {
                    int esperaSeg = 2;
                    if (res.Headers.RetryAfter?.Delta != null)
                        esperaSeg = (int)res.Headers.RetryAfter.Delta.Value.TotalSeconds;
                    else if (res.Headers.RetryAfter?.Date != null)
                        esperaSeg = (int)(res.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                    if (esperaSeg <= 0) esperaSeg = 2;

                    Program.EscribirLog($"[EMAIL] 429 recibido. Reintentando en {esperaSeg}s...");
                    await Task.Delay(TimeSpan.FromSeconds(esperaSeg));
                    continue;
                }

                // Otros errores: no reintentar
                string cuerpoError = await res.Content.ReadAsStringAsync();
                string correlacion = res.Headers.TryGetValues("X-Correlation-Id", out var vals)
                    ? vals.FirstOrDefault()
                    : "(sin correlación)";

                return new ResultadoEnvio
                {
                    Exito = false,
                    Error = $"El puente respondió {(int)res.StatusCode} {res.StatusCode}: {cuerpoError} (correlación {correlacion})"
                };
            }
            catch (HttpRequestException ex)
            {
                // Sin respuesta: no sabemos si el correo salió → no reintentar
                return new ResultadoEnvio
                {
                    Exito = false,
                    Error = $"No se pudo contactar con el puente: {ex.Message}"
                };
            }
            catch (TaskCanceledException)
            {
                return new ResultadoEnvio
                {
                    Exito = false,
                    Error = "Timeout al contactar con el puente de correo."
                };
            }
        }

        return new ResultadoEnvio
        {
            Exito = false,
            Error = "No se pudo enviar el correo: cuota agotada tras varios intentos."
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private class EmailRequest
    {
        [JsonPropertyName("to")]
        public string To { get; set; }

        [JsonPropertyName("subject")]
        public string Subject { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonPropertyName("cc")]
        public string Cc { get; set; }
    }

    private static string ObtenerValorConfig(Dictionary<string, string> config, string key, string section)
    {
        string fullKey = $"{section}.{key}";
        return config.TryGetValue(fullKey, out string value) ? value : null;
    }
}