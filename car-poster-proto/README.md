# Car Poster Prototype

This is a complete Streamlit backend prototype for generating a vehicle selling package from one uploaded image.

It demonstrates:

- Vehicle identification with Google Cloud Vision label detection.
- User verification of AI-extracted vehicle details.
- Listing copy generation with the OpenAI API.
- Poster composition with Pillow.
- A Streamlit interface for uploading, testing, and downloading the result.

## Setup

```bash
cd car-poster-proto
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt
cp .env.example .env
```

Edit `.env` and add:

```bash
OPENAI_API_KEY=your_openai_key_here
GOOGLE_APPLICATION_CREDENTIALS=google_creds.json
```

Place your Google service account key at:

```text
car-poster-proto/google_creds.json
```

The app will still run without API credentials. In that case it uses local fallback behavior so you can test the image handling, verification flow, text output, and poster creation.

## Run

```bash
streamlit run app.py
```

Open the Streamlit URL, upload a car image, click `Analyze Photo`, verify the fields, then click `Generate Selling Package`.

## Notes

- Generic Google Vision labels can usually infer broad details like "car", "SUV", or some makes, but not reliable exact year/model.
- Exact year/make/model detection should be upgraded later to a custom model or a VIN/license-plate workflow.
- The OpenAI call uses the modern Responses API. Set `OPENAI_MODEL=gpt-3.5-turbo` in `.env` if that model is available on your account and you want to mirror the original prototype exactly.
