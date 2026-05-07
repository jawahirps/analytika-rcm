import io
import os
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple

from dotenv import load_dotenv
from PIL import Image, ImageDraw, ImageFont, ImageOps

try:
    from google.cloud import vision
except ImportError:  # pragma: no cover - lets poster generation work without Vision installed
    vision = None

try:
    from openai import OpenAI
except ImportError:  # pragma: no cover - lets local checks report a friendly error
    OpenAI = None


load_dotenv()

COMMON_MAKES = {
    "acura": "Acura",
    "audi": "Audi",
    "bmw": "BMW",
    "buick": "Buick",
    "cadillac": "Cadillac",
    "chevrolet": "Chevrolet",
    "chevy": "Chevrolet",
    "chrysler": "Chrysler",
    "dodge": "Dodge",
    "ford": "Ford",
    "genesis": "Genesis",
    "gmc": "GMC",
    "honda": "Honda",
    "hyundai": "Hyundai",
    "infiniti": "Infiniti",
    "jaguar": "Jaguar",
    "jeep": "Jeep",
    "kia": "Kia",
    "land rover": "Land Rover",
    "lexus": "Lexus",
    "lincoln": "Lincoln",
    "mazda": "Mazda",
    "mercedes": "Mercedes-Benz",
    "mercedes-benz": "Mercedes-Benz",
    "mini": "MINI",
    "mitsubishi": "Mitsubishi",
    "nissan": "Nissan",
    "porsche": "Porsche",
    "ram": "Ram",
    "subaru": "Subaru",
    "tesla": "Tesla",
    "toyota": "Toyota",
    "volkswagen": "Volkswagen",
    "volvo": "Volvo",
}

BODY_TYPE_ALIASES = {
    "sedan": "Sedan",
    "sport utility vehicle": "SUV",
    "suv": "SUV",
    "crossover": "SUV",
    "pickup truck": "Truck",
    "truck": "Truck",
    "coupe": "Coupe",
    "convertible": "Convertible",
    "hatchback": "Hatchback",
    "wagon": "Wagon",
    "minivan": "Van",
    "van": "Van",
}


@dataclass
class VisionLabel:
    description: str
    score: float


def identify_vehicle(image_bytes: bytes) -> Dict[str, Any]:
    """Identify broad vehicle facts from a photo using Google Vision labels.

    Generic Vision labels are good at broad object context, not exact year/model.
    This returns a structured guess plus label evidence for the verification UI.
    """
    labels = _detect_google_labels(image_bytes)
    extracted_data: Dict[str, Any] = {
        "make": "Unknown",
        "model": "Unknown",
        "year": "Unknown",
        "body_type": "Unknown",
        "color": _estimate_dominant_color(image_bytes),
        "labels": [label.__dict__ for label in labels],
        "source": "google_vision" if labels else "local_fallback",
    }

    for label in labels:
        normalized = label.description.lower().strip()
        if extracted_data["make"] == "Unknown":
            for key, make in COMMON_MAKES.items():
                if key in normalized:
                    extracted_data["make"] = make
                    break

        if extracted_data["body_type"] == "Unknown":
            for key, body_type in BODY_TYPE_ALIASES.items():
                if key in normalized:
                    extracted_data["body_type"] = body_type
                    break

    if extracted_data["body_type"] == "Unknown":
        extracted_data["body_type"] = _infer_body_type_from_labels(labels)

    return extracted_data


def generate_listing_text(
    vehicle_data: Dict[str, str],
    mileage: str,
    price: str,
    style: str = "Professional",
) -> str:
    """Generate sales copy with OpenAI, falling back to a local template."""
    if _is_demo_mode() or not os.getenv("OPENAI_API_KEY") or OpenAI is None:
        return _local_listing_text(vehicle_data, mileage, price, style)

    model = os.getenv("OPENAI_MODEL", "gpt-4o-mini")
    client = OpenAI(api_key=os.getenv("OPENAI_API_KEY"))
    prompt = _build_listing_prompt(vehicle_data, mileage, price, style)

    try:
        response = client.responses.create(
            model=model,
            input=[
                {
                    "role": "system",
                    "content": "You are an expert automotive sales copywriter.",
                },
                {"role": "user", "content": prompt},
            ],
            temperature=0.7,
            max_output_tokens=280,
        )
        text = getattr(response, "output_text", "").strip()
        return text or _local_listing_text(vehicle_data, mileage, price, style)
    except Exception as exc:
        return (
            _local_listing_text(vehicle_data, mileage, price, style)
            + "\n\nOpenAI generation was unavailable: "
            + str(exc)
        )


def create_poster(image_bytes: bytes, vehicle_data: Dict[str, str], price: str) -> bytes:
    """Create a JPEG poster with readable sale overlays."""
    base = Image.open(io.BytesIO(image_bytes))
    base = ImageOps.exif_transpose(base).convert("RGB")
    base.thumbnail((1600, 1600), Image.Resampling.LANCZOS)

    canvas = base.convert("RGBA")
    width, height = canvas.size
    overlay = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)

    band_height = max(int(height * 0.27), 180)
    band_top = height - band_height
    draw.rectangle((0, band_top, width, height), fill=(12, 16, 20, 218))
    draw.rectangle((0, band_top, width, band_top + 8), fill=(43, 176, 118, 255))

    font_main = _load_font(max(34, int(height * 0.062)), bold=True)
    font_sub = _load_font(max(22, int(height * 0.034)), bold=False)
    font_badge = _load_font(max(26, int(height * 0.045)), bold=True)

    padding = max(24, int(width * 0.045))
    title = _clean_text(
        f"{vehicle_data.get('year', 'Unknown')} {vehicle_data.get('make', 'Unknown')} "
        f"{vehicle_data.get('model', 'Unknown')}"
    )
    subtitle = _clean_text(
        f"{vehicle_data.get('color', 'Unknown')} {vehicle_data.get('body_type', 'Vehicle')}"
    )

    max_text_width = int(width * 0.72)
    title_font = _fit_font(title, font_main, max_text_width, min_size=24, bold=True)
    subtitle_font = _fit_font(subtitle, font_sub, max_text_width, min_size=18, bold=False)

    draw.text((padding, band_top + 28), title, fill=(255, 255, 255, 255), font=title_font)
    draw.text(
        (padding, band_top + 40 + _text_height(title, title_font)),
        subtitle,
        fill=(215, 224, 230, 255),
        font=subtitle_font,
    )

    badge_text = _format_price(price)
    badge_pad_x = max(18, int(width * 0.018))
    badge_pad_y = max(12, int(height * 0.012))
    badge_font = _fit_font(badge_text, font_badge, int(width * 0.34), min_size=20, bold=True)
    badge_w = _text_width(badge_text, badge_font) + badge_pad_x * 2
    badge_h = _text_height(badge_text, badge_font) + badge_pad_y * 2
    badge_x = width - badge_w - padding
    badge_y = padding
    draw.rounded_rectangle(
        (badge_x, badge_y, badge_x + badge_w, badge_y + badge_h),
        radius=10,
        fill=(34, 143, 91, 245),
    )
    draw.text(
        (badge_x + badge_pad_x, badge_y + badge_pad_y - 2),
        badge_text,
        fill=(255, 255, 255, 255),
        font=badge_font,
    )

    output = Image.alpha_composite(canvas, overlay).convert("RGB")
    final_poster_buffer = io.BytesIO()
    output.save(final_poster_buffer, format="JPEG", quality=92, optimize=True)
    return final_poster_buffer.getvalue()


def _detect_google_labels(image_bytes: bytes) -> List[VisionLabel]:
    if _is_demo_mode() or vision is None:
        return []

    credentials_path = os.getenv("GOOGLE_APPLICATION_CREDENTIALS", "google_creds.json")
    if credentials_path and not os.path.isabs(credentials_path):
        credentials_path = os.path.join(os.path.dirname(__file__), credentials_path)

    if not credentials_path or not os.path.exists(credentials_path):
        return []

    os.environ["GOOGLE_APPLICATION_CREDENTIALS"] = credentials_path

    try:
        client = vision.ImageAnnotatorClient()
        image = vision.Image(content=image_bytes)
        response = client.label_detection(image=image, max_results=20)
        if response.error.message:
            return []
        return [
            VisionLabel(description=label.description, score=float(label.score))
            for label in response.label_annotations
        ]
    except Exception:
        return []


def _infer_body_type_from_labels(labels: List[VisionLabel]) -> str:
    label_text = " ".join(label.description.lower() for label in labels)
    if "vehicle" in label_text or "car" in label_text or "automotive" in label_text:
        return "Vehicle"
    return "Unknown"


def _estimate_dominant_color(image_bytes: bytes) -> str:
    try:
        image = Image.open(io.BytesIO(image_bytes))
        image = ImageOps.exif_transpose(image).convert("RGB")
        image.thumbnail((96, 96), Image.Resampling.LANCZOS)
        pixels = list(image.getdata())
        if not pixels:
            return "Unknown"
        r, g, b = _average_color(pixels)
        return _nearest_color_name((r, g, b))
    except Exception:
        return "Unknown"


def _average_color(pixels: List[Tuple[int, int, int]]) -> Tuple[int, int, int]:
    count = len(pixels)
    r = sum(pixel[0] for pixel in pixels) // count
    g = sum(pixel[1] for pixel in pixels) // count
    b = sum(pixel[2] for pixel in pixels) // count
    return r, g, b


def _nearest_color_name(rgb: Tuple[int, int, int]) -> str:
    palette = {
        "Black": (20, 20, 20),
        "White": (238, 238, 232),
        "Silver": (178, 184, 186),
        "Gray": (112, 119, 124),
        "Red": (170, 42, 42),
        "Blue": (46, 96, 170),
        "Green": (45, 126, 78),
        "Yellow": (222, 190, 63),
        "Orange": (215, 122, 48),
        "Brown": (116, 82, 54),
    }
    return min(
        palette,
        key=lambda name: sum((rgb[idx] - palette[name][idx]) ** 2 for idx in range(3)),
    )


def _build_listing_prompt(
    vehicle_data: Dict[str, str], mileage: str, price: str, style: str
) -> str:
    return f"""
Write a concise {style.lower()} classified ad description for a vehicle sale.

Vehicle details:
- Year/Make/Model: {vehicle_data.get('year', 'Unknown')} {vehicle_data.get('make', 'Unknown')} {vehicle_data.get('model', 'Unknown')}
- Body type: {vehicle_data.get('body_type', 'Unknown')}
- Color: {vehicle_data.get('color', 'Unknown')}
- Mileage: {mileage} miles
- Price: ${price}

Requirements:
- Mention the key details naturally.
- Keep it under 110 words.
- End with a clear call to action.
""".strip()


def _local_listing_text(
    vehicle_data: Dict[str, str], mileage: str, price: str, style: str
) -> str:
    vehicle_name = _clean_text(
        f"{vehicle_data.get('year', 'Unknown')} {vehicle_data.get('make', 'Unknown')} "
        f"{vehicle_data.get('model', 'Unknown')}"
    )
    body = vehicle_data.get("body_type", "vehicle")
    color = vehicle_data.get("color", "Unknown")
    tone = {
        "Professional": "Clean, well-presented, and ready for its next owner",
        "Exciting/Bold": "Stand out with a sharp look and confident road presence",
        "Quick Sale": "Priced to move and ready for a serious buyer",
    }.get(style, "Ready for its next owner")
    return (
        f"{vehicle_name} for sale. {tone}. This {color.lower()} {body.lower()} "
        f"has {mileage} miles and is listed at ${price}. Message today to schedule "
        f"a viewing or request more details."
    )


def _is_demo_mode() -> bool:
    return os.getenv("DEMO_MODE", "0").strip().lower() in {"1", "true", "yes", "on"}


def _load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    font_candidates = [
        os.path.join(os.path.dirname(__file__), "Arial.ttf"),
        "/Library/Fonts/Arial Bold.ttf" if bold else "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf"
        if bold
        else "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Helvetica.ttc",
        "/Library/Fonts/DejaVuSans-Bold.ttf" if bold else "/Library/Fonts/DejaVuSans.ttf",
    ]
    for path in font_candidates:
        if path and os.path.exists(path):
            return ImageFont.truetype(path, size=size)
    return ImageFont.load_default()


def _fit_font(
    text: str, font: ImageFont.ImageFont, max_width: int, min_size: int, bold: bool
) -> ImageFont.ImageFont:
    size = getattr(font, "size", min_size)
    while size > min_size and _text_width(text, font) > max_width:
        size -= 2
        font = _load_font(size, bold=bold)
    return font


def _text_width(text: str, font: ImageFont.ImageFont) -> int:
    box = font.getbbox(text)
    return box[2] - box[0]


def _text_height(text: str, font: ImageFont.ImageFont) -> int:
    box = font.getbbox(text)
    return box[3] - box[1]


def _format_price(price: str) -> str:
    clean_price = price.strip()
    if clean_price.startswith("$"):
        return clean_price
    return f"${clean_price}"


def _clean_text(value: str) -> str:
    return " ".join(str(value).split())
