FROM python:3.13-slim

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app

COPY pyproject.toml README.md ./
COPY juscraper ./juscraper
RUN pip install --no-cache-dir . \
    && useradd --system --uid 10001 juscraper

USER juscraper
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health')"
CMD ["uvicorn", "juscraper.api:app", "--host", "0.0.0.0", "--port", "8000"]
