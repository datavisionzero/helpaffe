import { FormEvent, useEffect, useState } from "react";
import { call, message, Project } from "./api";

type EmailSettings = {
  project_id: string;
  project_name: string;
  language: "en";
  smtp: { host: string; port: number; use_tls: boolean; username: string | null; password_configured: boolean };
  sender: { name: string; email: string };
  support_recipients: string[];
  branding: { name: string; logo_url: string | null; color: string };
  ticket_links: { customer: string; backoffice: string };
};

type EmailTemplate = {
  type: string;
  description: string;
  subject: string;
  text_body: string;
  html_body: string;
  variables: string[];
  is_customized: boolean;
};

type Preview = { subject: string; text_body: string; html_body: string };

export function EmailAdministration({ projects }: { projects: Project[] }) {
  const [projectId, setProjectId] = useState(projects[0]?.id ?? "");
  const [settings, setSettings] = useState<EmailSettings | null>(null);
  const [templates, setTemplates] = useState<EmailTemplate[]>([]);
  const [selectedType, setSelectedType] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [testRecipient, setTestRecipient] = useState("");
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    if (!projects.some(project => project.id === projectId)) setProjectId(projects[0]?.id ?? "");
  }, [projects, projectId]);

  useEffect(() => {
    if (projectId) void load(projectId);
    else {
      setSettings(null);
      setTemplates([]);
    }
  }, [projectId]);

  async function load(id: string) {
    try {
      setError("");
      const [loadedSettings, loadedTemplates] = await Promise.all([
        call<EmailSettings>(`/projects/${id}/email-settings`),
        call<EmailTemplate[]>(`/projects/${id}/email-templates`),
      ]);
      setSettings(loadedSettings);
      setTemplates(loadedTemplates);
      setSelectedType(current => loadedTemplates.some(template => template.type === current)
        ? current
        : loadedTemplates[0]?.type ?? "");
      setPreview(null);
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function saveSettings(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      setError("");
      setNotice("");
      const updated = await call<EmailSettings>(`/projects/${projectId}/email-settings`, {
        method: "PUT",
        body: JSON.stringify({
          language: "en",
          smtp: {
            host: data.get("smtpHost"),
            port: Number(data.get("smtpPort")),
            use_tls: data.get("smtpTls") === "on",
            username: data.get("smtpUsername") || null,
            password: data.get("smtpPassword") || null,
          },
          sender: { name: data.get("senderName"), email: data.get("senderEmail") },
          support_recipients: String(data.get("supportRecipients")).split(/[,\n]/).map(value => value.trim()).filter(Boolean),
          branding: { name: data.get("brandName"), logo_url: data.get("brandLogoUrl") || null, color: data.get("brandColor") },
          ticket_links: { customer: data.get("customerLink"), backoffice: data.get("backofficeLink") },
        }),
      });
      setSettings(updated);
      form.querySelector<HTMLInputElement>("[name=smtpPassword]")!.value = "";
      setNotice("Email settings saved.");
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function saveTemplate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    try {
      setError("");
      setNotice("");
      const saved = await call<EmailTemplate>(`/projects/${projectId}/email-templates/${selectedType}`, {
        method: "PUT",
        body: JSON.stringify({ subject: data.get("subject"), text_body: data.get("textBody"), html_body: data.get("htmlBody") }),
      });
      setTemplates(current => current.map(template => template.type === saved.type ? saved : template));
      setNotice("Email template saved.");
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function renderPreview() {
    try {
      setError("");
      setPreview(await call<Preview>(`/projects/${projectId}/email-templates/${selectedType}/preview`, {
        method: "POST",
        body: JSON.stringify({ ticket_number: "HLP-42", ticket_subject: "Example support request", customer_name: "Avery Customer", message: "This is example notification content." }),
      }));
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function sendTest() {
    try {
      setError("");
      setNotice("");
      await call(`/projects/${projectId}/email/test`, {
        method: "POST",
        body: JSON.stringify({ recipient: testRecipient, template_type: selectedType, sample: {} }),
      });
      setNotice(`Test email submitted to SMTP for ${testRecipient}.`);
    } catch (reason) {
      setError(message(reason));
    }
  }

  const template = templates.find(value => value.type === selectedType);
  return <section className="section email-admin" aria-labelledby="email-heading">
    <div className="section-heading"><div><p className="eyebrow">Notifications</p><h2 id="email-heading">Email delivery</h2></div><span className="status">English only</span></div>
    <p className="section-copy">Configure project-isolated SMTP delivery and the five fixed notification templates. Branding uses URLs and colors; file uploads are not supported.</p>
    {projects.length === 0 ? <p className="empty">Create a project before configuring email.</p> : <>
      <label className="email-project">Project<select aria-label="Email project" value={projectId} onChange={event => setProjectId(event.target.value)}>
        {projects.map(project => <option key={project.id} value={project.id}>{project.key} · {project.name}</option>)}
      </select></label>
      {error && <p className="error banner" role="alert">{error}</p>}
      {notice && <p className="notice" role="status">{notice}</p>}
      {settings && <form className="email-settings" key={settings.project_id} onSubmit={saveSettings}>
        <h3>Delivery and branding</h3>
        <div className="form-grid">
          <label>Language<select name="language" value="en" disabled><option value="en">English</option></select></label>
          <label>SMTP host<input name="smtpHost" required maxLength={255} defaultValue={settings.smtp.host} /></label>
          <label>SMTP port<input name="smtpPort" type="number" min={1} max={65535} required defaultValue={settings.smtp.port} /></label>
          <label className="check-row"><input name="smtpTls" type="checkbox" defaultChecked={settings.smtp.use_tls} />Use TLS</label>
          <label>SMTP username<input name="smtpUsername" maxLength={320} defaultValue={settings.smtp.username ?? ""} /></label>
          <label>SMTP password<input name="smtpPassword" type="password" autoComplete="new-password" placeholder={settings.smtp.password_configured ? "Configured — leave blank to keep" : "Set password"} /></label>
          <label>Sender name<input name="senderName" required maxLength={200} defaultValue={settings.sender.name} /></label>
          <label>Sender email<input name="senderEmail" type="email" required maxLength={320} defaultValue={settings.sender.email} /></label>
          <label className="span-two">Support recipients<textarea name="supportRecipients" required rows={3} defaultValue={settings.support_recipients.join("\n")} /></label>
          <label>Brand name<input name="brandName" required maxLength={200} defaultValue={settings.branding.name} /></label>
          <label>Brand color<input name="brandColor" required pattern="#[0-9A-Fa-f]{6}" defaultValue={settings.branding.color} /></label>
          <label className="span-two">Logo URL<input name="brandLogoUrl" type="url" maxLength={2048} defaultValue={settings.branding.logo_url ?? ""} placeholder="https://cdn.example/logo.png" /></label>
          <label className="span-two">Customer ticket link<input name="customerLink" required maxLength={2048} defaultValue={settings.ticket_links.customer} placeholder="https://product.example/support/{{ticket_number}}" /></label>
          <label className="span-two">Backoffice ticket link<input name="backofficeLink" required maxLength={2048} defaultValue={settings.ticket_links.backoffice} placeholder="https://support.example/tickets/{{ticket_number}}" /></label>
        </div>
        <button type="submit">Save email settings</button>
      </form>}
      {template && <div className="template-admin">
        <label>Template<select aria-label="Email template" value={selectedType} onChange={event => { setSelectedType(event.target.value); setPreview(null); }}>
          {templates.map(value => <option key={value.type} value={value.type}>{value.type}{value.is_customized ? " · customized" : " · default"}</option>)}
        </select></label>
        <p className="section-copy">{template.description}</p>
        <p className="template-variables">Variables: {template.variables.map(variable => `{{${variable}}}`).join(", ")}</p>
        <form key={`${projectId}-${template.type}-${template.is_customized}`} onSubmit={saveTemplate}>
          <label>Subject<input name="subject" required maxLength={300} defaultValue={template.subject} /></label>
          <label>Text body<textarea name="textBody" required rows={8} defaultValue={template.text_body} /></label>
          <label>HTML body<textarea name="htmlBody" required rows={10} defaultValue={template.html_body} /></label>
          <div className="email-actions"><button type="submit">Save template</button><button type="button" className="secondary" onClick={() => void renderPreview()}>Preview</button></div>
        </form>
        {preview && <div className="email-preview"><h4>{preview.subject}</h4><pre>{preview.text_body}</pre><iframe title="Email HTML preview" sandbox="" srcDoc={preview.html_body} /></div>}
        <div className="email-test"><label>Test recipient<input type="email" value={testRecipient} onChange={event => setTestRecipient(event.target.value)} placeholder="you@example.test" /></label><button type="button" disabled={!testRecipient} onClick={() => void sendTest()}>Send test email</button></div>
      </div>}
    </>}
  </section>;
}
