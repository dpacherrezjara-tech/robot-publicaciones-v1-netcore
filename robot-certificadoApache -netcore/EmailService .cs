using CertificadoAutomaticoXampp;
using System.Net;
using System.Net.Mail;

namespace CertificadoAutomatico;

public class EmailService
{
    private readonly SmtpClient _smtpClient;
    private readonly string _from;
    private readonly string _to;
    private readonly string _cc;
    private readonly string _subject;
    private readonly string _environmentName;
    private readonly bool _enabled;

    public EmailService(Dictionary<string, string> config)
    {
        string enable = ObtenerValorConfig(config, "ENABLE_EMAIL", "EMAIL_SETTINGS") ?? "false";
        _enabled = enable.ToLower() == "true";

        if (!_enabled)
            return;

        string host = ObtenerValorConfig(config, "SMTP_HOST", "EMAIL_SETTINGS") ?? "smtp.office365.com";
        int port = int.TryParse(ObtenerValorConfig(config, "SMTP_PORT", "EMAIL_SETTINGS"), out int p) ? p : 587;
        string user = ObtenerValorConfig(config, "SMTP_USER", "EMAIL_SETTINGS");
        string password = ObtenerValorConfig(config, "SMTP_PASSWORD", "EMAIL_SETTINGS");
        _from = ObtenerValorConfig(config, "EMAIL_FROM", "EMAIL_SETTINGS") ?? user;
        _to = ObtenerValorConfig(config, "EMAIL_TO", "EMAIL_SETTINGS");
        _cc = ObtenerValorConfig(config, "EMAIL_CC", "EMAIL_SETTINGS");
        string subjectTemplate = ObtenerValorConfig(config, "EMAIL_SUBJECT", "EMAIL_SETTINGS") ?? "Actualización Certificados SSL - [ENTORNO]";

        _environmentName = ObtenerValorConfig(config, "ENV_NAME", "ENVIRONMENT_SETTINGS") ?? "Sin Entorno";
        _subject = subjectTemplate.Replace("[ENTORNO]", _environmentName);

        _smtpClient = new SmtpClient(host, port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(user, password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = 30000
        };
    }

    public bool SendSuccessNotification(string expiryDate, string serverName, string estado, string colorEstado, out string error)
    {
        error = "";
        if (!_enabled)
        {
            Program.EscribirLog("[EMAIL] Notificaciones por correo desactivadas.");
            return true;
        }

        if (string.IsNullOrEmpty(_to))
        {
            error = "No se especificó destinatario (EMAIL_TO)";
            return false;
        }

        try
        {
            string fechaActual = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            string fechaExpiracion = string.IsNullOrEmpty(expiryDate) ? "No disponible" : expiryDate;
            string servidor = string.IsNullOrEmpty(serverName) ? Environment.MachineName : serverName;

            string body = $@"
            <div style='background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 20px; font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
                <h3 style='margin-top: 0; color: #2b6cb0; border-bottom: 1px solid #edf2f7; padding-bottom: 10px;'>
                    Reporte de Actualización SSL - {_environmentName}
                </h3>
                
                <table style='width: 100%; font-size: 14px; color: #2d3748; border-collapse: collapse;'>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Servidor / SSL:</td>
                        <td style='padding: 8px 0;'>{servidor}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Fecha de Ejecución:</td>
                        <td style='padding: 8px 0;'>{fechaActual}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Estado:</td>
                        <td style='padding: 8px 0; color: {colorEstado}; font-weight: bold;'>{estado}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px 0; font-weight: bold;'>Nueva Caducidad:</td>
                        <td style='padding: 8px 0; color: #e53e3e; font-weight: bold;'>{fechaExpiracion}</td>
                    </tr>
                </table>

                <div style='margin-top: 20px; padding-top: 10px; border-top: 1px solid #edf2f7; font-size: 12px; color: #718096; text-align: center;'>
                    Este es un mensaje automático generado por el sistema de actualización de certificados.<br />
                    Por favor, no responder a este correo.
                </div>
            </div>";

            using var message = new MailMessage
            {
                From = new MailAddress(_from),
                Subject = _subject,
                Body = body,
                IsBodyHtml = true
            };

            foreach (var to in _to.Split(';', StringSplitOptions.RemoveEmptyEntries))
                message.To.Add(to.Trim());

            if (!string.IsNullOrEmpty(_cc))
            {
                foreach (var cc in _cc.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    message.CC.Add(cc.Trim());
            }

            _smtpClient.Send(message);
            Program.EscribirLog($"[EMAIL] Notificación enviada a: {_to}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Error al enviar correo: {ex.Message}";
            return false;
        }
    }

    private static string ObtenerValorConfig(Dictionary<string, string> config, string key, string section)
    {
        string fullKey = $"{section}.{key}";
        return config.TryGetValue(fullKey, out string value) ? value : null;
    }
}