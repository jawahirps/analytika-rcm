from __future__ import annotations

import os
from dataclasses import dataclass, field
from typing import Dict, List

from fastapi import FastAPI, Form, HTTPException
from fastapi.responses import Response
from openai import OpenAI
from twilio.rest import Client
from twilio.twiml.voice_response import Gather, VoiceResponse


@dataclass
class CallState:
    history: List[dict] = field(default_factory=list)


app = FastAPI(title="Ghaf Services AI Calling Bot")

COMPANY_NAME = os.environ.get("COMPANY_NAME", "Ghaf Services")
COMPANY_DOMAIN = os.environ.get("COMPANY_DOMAIN", "ghafservices.com")
VOICE_NAME = os.environ.get("VOICE_NAME", "Polly.Joanna")
DEFAULT_LANGUAGE = os.environ.get("DEFAULT_LANGUAGE", "en-US")

CALL_STATES: Dict[str, CallState] = {}

SYSTEM_PROMPT = (
    "You are an AI calling assistant for Ghaf Services."
    " Be concise, friendly, and professional."
    " Answer questions about services, booking, pricing requests,"
    " and collect caller name, phone, and service needs."
    " If you need to hand off to a human, ask for the best callback number"
    " and preferred time."
)


@app.get("/health")
def health() -> dict:
    return {"status": "ok"}


@app.post("/voice")
def voice(call_sid: str = Form(default="", alias="CallSid")) -> Response:
    response = VoiceResponse()

    if not call_sid:
        raise HTTPException(status_code=400, detail="Missing CallSid")

    CALL_STATES.setdefault(call_sid, CallState())

    response.say(
        f"Hello, thank you for calling {COMPANY_NAME}. How can I help you today?",
        voice=VOICE_NAME,
        language=DEFAULT_LANGUAGE,
    )

    gather = Gather(
        input="speech",
        action="/gather",
        method="POST",
        timeout=6,
        speech_timeout="auto",
        language=DEFAULT_LANGUAGE,
    )
    gather.say(
        "Please tell me how I can assist.",
        voice=VOICE_NAME,
        language=DEFAULT_LANGUAGE,
    )
    response.append(gather)
    response.redirect("/voice")

    return Response(content=str(response), media_type="application/xml")


@app.post("/gather")
def gather(
    call_sid: str = Form(default="", alias="CallSid"),
    speech_result: str = Form(default="", alias="SpeechResult"),
    confidence: str = Form(default="", alias="Confidence"),
) -> Response:
    if not call_sid:
        raise HTTPException(status_code=400, detail="Missing CallSid")

    state = CALL_STATES.setdefault(call_sid, CallState())
    prompt = speech_result or ""

    if not prompt.strip():
        return _retry_response("I didn't catch that. Could you repeat?")

    state.history.append({"role": "user", "content": prompt})
    assistant_reply = generate_ai_reply(state.history)
    state.history.append({"role": "assistant", "content": assistant_reply})

    response = VoiceResponse()
    response.say(assistant_reply, voice=VOICE_NAME, language=DEFAULT_LANGUAGE)

    gather_twiml = Gather(
        input="speech",
        action="/gather",
        method="POST",
        timeout=6,
        speech_timeout="auto",
        language=DEFAULT_LANGUAGE,
    )
    gather_twiml.say(
        "Is there anything else I can help you with?",
        voice=VOICE_NAME,
        language=DEFAULT_LANGUAGE,
    )
    response.append(gather_twiml)
    response.redirect("/gather")

    return Response(content=str(response), media_type="application/xml")


@app.post("/outbound")
def outbound_call(to_number: str = Form(default="")) -> dict:
    if not to_number:
        raise HTTPException(status_code=400, detail="Missing phone number")

    account_sid = os.environ.get("TWILIO_ACCOUNT_SID")
    auth_token = os.environ.get("TWILIO_AUTH_TOKEN")
    from_number = os.environ.get("TWILIO_PHONE_NUMBER")
    public_url = os.environ.get("PUBLIC_URL")

    if not all([account_sid, auth_token, from_number, public_url]):
        raise HTTPException(status_code=500, detail="Missing Twilio configuration")

    client = Client(account_sid, auth_token)
    call = client.calls.create(
        to=to_number,
        from_=from_number,
        url=f"{public_url}/voice",
    )

    return {"status": "initiated", "call_sid": call.sid}


def _retry_response(message: str) -> Response:
    response = VoiceResponse()
    response.say(message, voice=VOICE_NAME, language=DEFAULT_LANGUAGE)

    gather = Gather(
        input="speech",
        action="/gather",
        method="POST",
        timeout=6,
        speech_timeout="auto",
        language=DEFAULT_LANGUAGE,
    )
    gather.say(
        "Please tell me how I can assist.",
        voice=VOICE_NAME,
        language=DEFAULT_LANGUAGE,
    )
    response.append(gather)
    response.redirect("/gather")

    return Response(content=str(response), media_type="application/xml")


def generate_ai_reply(history: List[dict]) -> str:
    messages = [{"role": "system", "content": SYSTEM_PROMPT}]
    messages.extend(history)

    openai_client = _get_openai_client()
    completion = openai_client.chat.completions.create(
        model=os.environ.get("OPENAI_MODEL", "gpt-4o-mini"),
        messages=messages,
        max_tokens=200,
        temperature=0.4,
    )

    reply = completion.choices[0].message.content
    if not reply:
        return (
            f"Thanks for calling {COMPANY_NAME}."
            " I will pass your request to our team."
        )

    return reply.strip()


def _get_openai_client() -> OpenAI:
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("OPENAI_API_KEY is not configured.")
    return OpenAI(api_key=api_key)


@app.get("/")
def home() -> dict:
    return {
        "service": "ghafservices-ai-calling-bot",
        "company": COMPANY_NAME,
        "domain": COMPANY_DOMAIN,
    }
