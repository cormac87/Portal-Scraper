import json
import os
import re
import string
import sys
from collections import defaultdict


MAX_CHUNK_CHARS = 200_000
TRIM_CHARS = string.whitespace + ".,;:!?()[]{}<>\"'"
WHITESPACE_RE = re.compile(r"\s+")


def main() -> int:
    configure_stdio()

    try:
        request_text = sys.stdin.buffer.read().decode("utf-8-sig")
        request = json.loads(request_text)
    except json.JSONDecodeError as exc:
        print(f"Unable to read JSON request: {exc}", file=sys.stderr)
        return 2

    try:
        nlp = load_spacy_model()
    except Exception as exc:
        print(f"Unable to load spaCy model: {exc}", file=sys.stderr)
        return 3

    documents = request.get("documents") or []
    organisations = {}
    skipped_document_count = 0
    processed_document_count = 0

    for document in documents:
        document_id = document.get("id") or ""
        document_name = document.get("name") or "Untitled document"
        text = document.get("text") or ""

        if not text.strip():
            skipped_document_count += 1
            continue

        processed_document_count += 1
        document_mentions = defaultdict(int)

        for chunk in iter_text_chunks(text):
            for entity in nlp(chunk).ents:
                if entity.label_ != "ORG":
                    continue

                organisation_name = clean_organisation_name(entity.text)
                if organisation_name is None:
                    continue

                key = normalise_key(organisation_name)
                if key not in organisations:
                    organisations[key] = {
                        "name": organisation_name,
                        "mentionCount": 0,
                        "documents": {},
                    }

                organisations[key]["mentionCount"] += 1
                document_mentions[key] += 1

        for key, mention_count in document_mentions.items():
            organisations[key]["documents"][document_id] = {
                "documentId": document_id,
                "documentName": document_name,
                "mentionCount": mention_count,
            }

    organisation_matches = []
    for organisation in organisations.values():
        document_matches = sorted(
            organisation["documents"].values(),
            key=lambda match: (-match["mentionCount"], match["documentName"].casefold()),
        )
        organisation_matches.append(
            {
                "name": organisation["name"],
                "mentionCount": organisation["mentionCount"],
                "documentCount": len(document_matches),
                "documents": document_matches,
            }
        )

    organisation_matches.sort(
        key=lambda match: (-match["mentionCount"], match["name"].casefold())
    )

    response = {
        "organisations": organisation_matches,
        "processedDocumentCount": processed_document_count,
        "skippedDocumentCount": skipped_document_count,
        "totalMentionCount": sum(match["mentionCount"] for match in organisation_matches),
    }

    json.dump(response, sys.stdout, ensure_ascii=False)
    return 0


def configure_stdio() -> None:
    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except AttributeError:
        pass


def load_spacy_model():
    import spacy

    model_name = os.environ.get("SPACY_MODEL", "en_core_web_sm")
    disabled_components = ["tagger", "parser", "attribute_ruler", "lemmatizer"]
    return spacy.load(model_name, disable=disabled_components)


def iter_text_chunks(text: str):
    compacted_text = WHITESPACE_RE.sub(" ", text).strip()
    start = 0
    text_length = len(compacted_text)

    while start < text_length:
        end = min(start + MAX_CHUNK_CHARS, text_length)
        if end < text_length:
            split_at = max(
                compacted_text.rfind(". ", start, end),
                compacted_text.rfind(" ", start, end),
            )
            if split_at > start + (MAX_CHUNK_CHARS // 2):
                end = split_at + 1

        chunk = compacted_text[start:end].strip()
        if chunk:
            yield chunk

        start = end


def clean_organisation_name(value: str):
    name = WHITESPACE_RE.sub(" ", value).strip(TRIM_CHARS)
    if len(name) < 2 or name.isdigit():
        return None

    return name


def normalise_key(value: str) -> str:
    return WHITESPACE_RE.sub(" ", value).strip().casefold()


if __name__ == "__main__":
    raise SystemExit(main())
