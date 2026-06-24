# Ghaf Services AI Calling Bot

This service provides a voice assistant for **ghafservices.com** using Twilio Programmable Voice and OpenAI. It supports inbound calls via a `/voice` webhook and outbound calls via the `/outbound` endpoint.

## Features
- Inbound call handling with speech recognition.
- AI responses tailored to Ghaf Services.
- Outbound calling endpoint for follow-ups.
- Simple configuration via environment variables.

## Requirements
- Python 3.10+
- A Twilio account with a voice-enabled phone number.
- An OpenAI API key.
- A public URL (ngrok, Fly.io, Render, etc.) for Twilio webhooks.

## Setup

```bash
cd ai_calling_bot
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

Create a `.env` file using the template below.

```bash
cp .env.example .env
```

## Environment Variables

- `OPENAI_API_KEY`: OpenAI API key.
- `OPENAI_MODEL`: Model name (default: `gpt-4o-mini`).
- `TWILIO_ACCOUNT_SID`: Twilio account SID.
- `TWILIO_AUTH_TOKEN`: Twilio auth token.
- `TWILIO_PHONE_NUMBER`: Twilio phone number (E.164).
- `PUBLIC_URL`: Public base URL (e.g., `https://your-tunnel.ngrok.app`).
- `COMPANY_NAME`: Display name (default: `Ghaf Services`).
- `COMPANY_DOMAIN`: Domain (default: `ghafservices.com`).
- `VOICE_NAME`: Voice to use (default: `Polly.Joanna`).
- `DEFAULT_LANGUAGE`: Twilio speech language (default: `en-US`).

## Run Locally

```bash
uvicorn ai_calling_bot.app:app --host 0.0.0.0 --port 8000
```

Expose the server using a tunneling tool and set your Twilio number webhook:

- **Voice webhook**: `https://<public-url>/voice`

## Outbound Calls

Trigger a call by POSTing to `/outbound`:

```bash
curl -X POST https://<public-url>/outbound \
  -d "to_number=+15551234567"
```

## Notes
- The bot stores conversation state in memory per call. For production, replace this with a persistent store (Redis, Postgres, etc.).
- Use a production process manager (systemd, Docker, etc.) and HTTPS for webhooks.

## Tests

```bash
pip install -r requirements-dev.txt
pytest
```
