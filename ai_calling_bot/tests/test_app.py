import pathlib
import sys

from fastapi.testclient import TestClient

PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.append(str(PROJECT_ROOT))

import ai_calling_bot.app as app_module  # noqa: E402


client = TestClient(app_module.app)


def test_health() -> None:
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_home() -> None:
    response = client.get("/")
    assert response.status_code == 200
    assert response.json()["service"] == "ghafservices-ai-calling-bot"


def test_voice_requires_call_sid() -> None:
    response = client.post("/voice")
    assert response.status_code == 400


def test_voice_response() -> None:
    response = client.post("/voice", data={"CallSid": "CA123"})
    assert response.status_code == 200
    assert "Hello, thank you for calling" in response.text


def test_gather_requires_call_sid() -> None:
    response = client.post("/gather")
    assert response.status_code == 400


def test_gather_with_speech(monkeypatch) -> None:
    def fake_reply(history):
        return "We offer cleaning services. Would you like to book?"

    monkeypatch.setattr(app_module, "generate_ai_reply", fake_reply)

    response = client.post(
        "/gather",
        data={
            "CallSid": "CA123",
            "SpeechResult": "I want to know your services",
            "Confidence": "0.9",
        },
    )
    assert response.status_code == 200
    assert "We offer cleaning services" in response.text


def test_outbound_call(monkeypatch) -> None:
    class FakeCall:
        sid = "CA999"

    class FakeCalls:
        def create(self, to, from_, url):
            assert to == "+15551234567"
            assert from_ == "+15550001111"
            assert url == "https://example.com/voice"
            return FakeCall()

    class FakeClient:
        def __init__(self, account_sid, auth_token):
            assert account_sid == "AC123"
            assert auth_token == "secret"
            self.calls = FakeCalls()

    monkeypatch.setattr(app_module, "Client", FakeClient)
    monkeypatch.setenv("TWILIO_ACCOUNT_SID", "AC123")
    monkeypatch.setenv("TWILIO_AUTH_TOKEN", "secret")
    monkeypatch.setenv("TWILIO_PHONE_NUMBER", "+15550001111")
    monkeypatch.setenv("PUBLIC_URL", "https://example.com")

    response = client.post("/outbound", data={"to_number": "+15551234567"})
    assert response.status_code == 200
    assert response.json() == {"status": "initiated", "call_sid": "CA999"}


def test_outbound_missing_config(monkeypatch) -> None:
    for key in [
        "TWILIO_ACCOUNT_SID",
        "TWILIO_AUTH_TOKEN",
        "TWILIO_PHONE_NUMBER",
        "PUBLIC_URL",
    ]:
        monkeypatch.delenv(key, raising=False)

    response = client.post("/outbound", data={"to_number": "+15551234567"})
    assert response.status_code == 500
