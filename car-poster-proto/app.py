import hashlib

import streamlit as st

from engine import create_poster, generate_listing_text, identify_vehicle


st.set_page_config(page_title="Car Post Generator", page_icon="car", layout="wide")


def _file_digest(file_bytes: bytes) -> str:
    return hashlib.sha256(file_bytes).hexdigest()


def _reset_for_new_upload(file_bytes: bytes) -> None:
    digest = _file_digest(file_bytes)
    if st.session_state.get("image_digest") != digest:
        st.session_state.image_digest = digest
        st.session_state.raw_identification = None
        st.session_state.listing_text = ""
        st.session_state.poster_bytes = None


def _vehicle_defaults(raw_data):
    return {
        "year": "2019" if raw_data.get("year") == "Unknown" else raw_data.get("year", "2019"),
        "make": raw_data.get("make", "Unknown"),
        "model": "Camry SE" if raw_data.get("model") == "Unknown" else raw_data.get("model", "Camry SE"),
        "body_type": raw_data.get("body_type", "Unknown"),
        "color": raw_data.get("color", "Unknown"),
    }


st.title("Vehicle Selling Post Generator")
st.caption("Upload a vehicle photo, verify the AI guess, then generate listing copy and a poster.")

with st.sidebar:
    st.header("Upload")
    uploaded_file = st.file_uploader("Car image", type=["jpg", "jpeg", "png"])

    st.header("Sale Details")
    price_input = st.text_input("Selling price", value="15,000")
    mileage_input = st.text_input("Mileage", value="80,000")
    style_input = st.selectbox("Writing style", ["Professional", "Exciting/Bold", "Quick Sale"])

    st.divider()
    analyze_btn = st.button("Analyze Photo", type="primary", disabled=uploaded_file is None)

if uploaded_file is None:
    st.info("Upload a JPG or PNG vehicle image to begin.")
    st.stop()

image_bytes = uploaded_file.getvalue()
_reset_for_new_upload(image_bytes)

left_col, right_col = st.columns([0.95, 1.05], gap="large")

with left_col:
    st.subheader("Original Image")
    st.image(image_bytes, use_container_width=True)

if analyze_btn:
    with st.spinner("Reading the image with Google Vision when credentials are available..."):
        st.session_state.raw_identification = identify_vehicle(image_bytes)
        st.session_state.listing_text = ""
        st.session_state.poster_bytes = None

raw_identification = st.session_state.get("raw_identification")

with right_col:
    if raw_identification is None:
        st.subheader("Vehicle Details")
        st.write("Click Analyze Photo to extract initial make, body type, color, and label evidence.")
        st.stop()

    defaults = _vehicle_defaults(raw_identification)
    source_label = "Google Vision" if raw_identification.get("source") == "google_vision" else "local fallback"
    st.success(f"Analysis complete using {source_label}. Verify the details below.")

    form_col_1, form_col_2 = st.columns(2)
    with form_col_1:
        final_year = st.text_input("Year", value=defaults["year"])
        final_make = st.text_input("Make", value=defaults["make"])
        final_color = st.text_input("Color", value=defaults["color"])
    with form_col_2:
        final_model = st.text_input("Model", value=defaults["model"])
        final_body = st.text_input("Body type", value=defaults["body_type"])

    verified_data = {
        "year": final_year,
        "make": final_make,
        "model": final_model,
        "body_type": final_body,
        "color": final_color,
    }

    generate_btn = st.button("Generate Selling Package", type="primary")

    if raw_identification.get("labels"):
        with st.expander("Vision label evidence"):
            for label in raw_identification["labels"][:10]:
                st.write(f"{label['description']} - {label['score']:.0%}")

if generate_btn:
    with st.spinner("Writing the listing and composing the poster..."):
        st.session_state.listing_text = generate_listing_text(
            verified_data, mileage_input, price_input, style_input
        )
        st.session_state.poster_bytes = create_poster(image_bytes, verified_data, price_input)

if st.session_state.get("listing_text") and st.session_state.get("poster_bytes"):
    st.divider()
    st.header("Generated Selling Package")

    result_text_col, result_poster_col = st.columns([0.95, 1.05], gap="large")
    with result_text_col:
        st.subheader("Listing Description")
        st.text_area("Generated copy", value=st.session_state.listing_text, height=260)

    with result_poster_col:
        st.subheader("Poster")
        st.image(st.session_state.poster_bytes, use_container_width=True)
        st.download_button(
            "Download Poster",
            data=st.session_state.poster_bytes,
            file_name="car_poster.jpg",
            mime="image/jpeg",
        )
