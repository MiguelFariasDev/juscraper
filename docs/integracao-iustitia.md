# Integração Iustitia ↔ juscraper

> **Como usar este documento:** cole-o inteiro no agente do Iustitia (ou anexe-o), junto com o
> patch `0001-acompanhamento-de-movimentacoes.patch`, se você o tiver. A seção 1 é o pedido; as
> seções seguintes são a especificação de referência que o agente deve seguir.

---

## 1. Prompt para o agente do Iustitia

Você vai integrar o Iustitia ao **juscraper**, um serviço HTTP (.NET 10) que consulta processos e
movimentações diretamente nos portais públicos dos tribunais (e-SAJ e a nova consulta pública do
PJe). O contrato completo está nas seções 2 a 6 deste documento. Siga as convenções do
`claude.md` do Iustitia: Result Pattern, `ErrorFactory`/`ErrorCatalog`, nunca logar conteúdo de
processo, RLS por tenant, revisão humana obrigatória e marca desacoplada.

**Objetivos:**

1. **O juscraper passa a ser a fonte principal** da consulta de processo no tribunal
   (`IConsultaProcessoTribunal`, usada no auto-preenchimento do cadastro). **O DataJud (API
   Pública do CNJ) passa a ser o fallback**, usado só quando o juscraper der erro ou não cobrir o
   tribunal, conforme a matriz da seção 5. Implemente com um adaptador composto (seção 5.2). O
   handler `ConsultarProcessoTribunalHandler`, o endpoint e a tela não devem mudar, exceto pelo
   campo opcional `fonte` descrito em 5.4.

2. **Acompanhamento noturno de movimentações** dos processos ativos e suspensos, com alerta ao
   advogado responsável e aviso ao cliente **somente após a aprovação do advogado** (seção 6):
   - Se o patch `0001-acompanhamento-de-movimentacoes.patch` foi fornecido, aplique-o primeiro
     (`git am`, gerado sobre `ef374f4`). Ele já contém o domínio, o job, os endpoints, a migration,
     o SQL de RLS, os testes e o ADR-074, além do adaptador do juscraper.
   - Sem o patch, implemente a seção 6 do zero.
   - Em seguida, aplique a regra de fallback do acompanhamento (6.4).

3. **Atualize a documentação:**
   - ADR novo, ou o ADR-074 do patch, registrando juscraper como fonte principal e DataJud como
     fallback, e revendo o ADR-064;
   - `docs/integrations/` com a configuração;
   - catálogo de erros, se criar códigos.

4. **Testes:** escreva os testes da seção 7 e rode a suíte unitária e de arquitetura. Os testes
   de integração dependem de PostgreSQL; rode-os se o ambiente tiver o banco.

**Não faça:**
- não remova o DataJud;
- não ligue o job por padrão;
- não envie nada ao cliente sem revisão;
- não versione chave de API.

**Entregue:** um branch com commits pequenos, um resumo do que mudou e a lista do que não pôde
ser testado.

---

## 2. O serviço

| Item | Valor |
|---|---|
| URL base | configurável, por exemplo `http://juscraper:8080/` (docker-compose) ou a URL interna no Azure Container Apps. **Hospede na região Brazil South**: vários tribunais recusam acessos de fora do Brasil |
| Autenticação | header `X-Api-Key: <chave>`. As chaves aceitas ficam em `Juscraper__Api__ApiKeys` no serviço, separadas por vírgula. Sem chave válida: `401` |
| Formato | JSON camelCase; datas `yyyy-MM-dd`; data/hora ISO 8601 com fuso (`2026-11-19T17:45:11-03:00`) |
| OpenAPI | `GET /openapi/v1.json` |
| Health | `GET /health` (sem autenticação). **Não** use como readiness do Iustitia: o juscraper é uma dependência externa, como o CNJ (ver `CnjHealthCheck`, tag `cnj`) |

### Latência e carga

- O juscraper espera **1 segundo entre requisições ao mesmo portal** e faz no máximo 2 ao mesmo
  tempo. Com isso, uma consulta fria do TJSP com cerca de 50 andamentos leva uns 13 s. Com
  `max_movimentacoes=10`, leva de 2 a 4 s.
- O cache é de 30 min, e o "não encontrado" também fica em cache.
- O juscraper já faz retry com backoff contra o portal. No Iustitia, use **no máximo uma** nova
  tentativa: repetir mais multiplicaria as requisições ao tribunal.
- Timeouts recomendados no `AddStandardResilienceHandler`:
  - **consulta do cadastro:** 60 s por tentativa (há um usuário esperando, que já esperava de
    12 a 14 s pelo DataJud);
  - **acompanhamento:** 3 min por tentativa, porque a primeira sincronização traz o histórico
    inteiro.

## 3. Endpoints

`{numero}` é o número CNJ, com ou sem máscara. **Envie os 20 dígitos** (`CNJNumber.Digits`). O
tribunal **não** é parâmetro: o juscraper o deriva do número, com o mesmo algoritmo do
`TribunalResolver`.

### 3.1 `GET /v1/processos/{numero}`: consulta completa

| Query | Padrão | Uso |
|---|---|---|
| `instancias` | `1,2` | `1` = 1º grau, `2` = 2º grau. Só o e-SAJ separa as instâncias |
| `max_movimentacoes` | todas | Para de paginar o portal ao atingir N **por instância**. Use `10` na consulta do cadastro |
| `sem_cache` | `false` | Ignora o cache |

Resposta `200`:

```json
{
  "numeroProcesso": "0700482-85.2026.8.02.0349",
  "tribunal": "TJAL",
  "encontrado": true,
  "processos": [
    {
      "numeroProcesso": "0700482-85.2026.8.02.0349",
      "tribunal": "TJAL",
      "grau": "G1",
      "sistema": "e-SAJ",
      "dataAjuizamento": "2026-06-02",
      "emSegredoDeJustica": false,
      "classe": "Procedimento do Juizado Especial Cível",
      "orgaoJulgador": "Juizado Esp. Cível e Criminal … de Penedo",
      "assuntos": ["Perdas e Danos"],
      "movimentos": [
        {
          "id": "522a82264e6d364ce2d152432e2316e9",
          "nome": "Certidão de Arquivamento Sem Custas a recolher",
          "dataHora": "2026-09-22T00:00:00-03:00",
          "complemento": "Certidão de Arquivamento Sem Custas a Recolher",
          "possuiDocumento": true,
          "instancia": "G1"
        }
      ],
      "codigoMunicipioIbge": null,
      "situacao": "Baixado",
      "area": "Cível",
      "foro": "Juizado de Penedo",
      "juiz": "…",
      "valorDaCausa": "R$ 591,34",
      "partes": [
        { "nome": "…", "tipo": "Autor", "polo": "ATIVO", "advogados": [{ "nome": "…", "oab": null }] }
      ],
      "urlConsulta": "https://www2.tjal.jus.br/cpopg/show.do?processo.codigo=…",
      "movimentosCompletos": false
    }
  ],
  "consultadoEm": "2026-11-25T17:07:58.66+00:00",
  "avisos": []
}
```

Regras:

- **`encontrado: false` com HTTP 200 é desfecho normal**: o portal respondeu e não conhece o
  processo. Corresponde ao `Result.Success(null)` da porta, como no ADR-064.
- Há **um item em `processos` por instância** (o mesmo número pode estar no 1º e no 2º grau).
  Para o cadastro, use os metadados do `G1` quando existir e junte os `movimentos` de todas as
  instâncias, ordenando por `dataHora` decrescente.
- Os campos `numeroProcesso`, `tribunal`, `grau`, `dataAjuizamento`, `emSegredoDeJustica`,
  `classe`, `sistema`, `orgaoJulgador`, `assuntos`, `movimentos[].nome`/`dataHora` e
  `codigoMunicipioIbge` têm **os mesmos nomes e significados do `ProcessoTribunal` e do
  `MovimentoProcesso`**.
- `movimentos[].complemento` é o texto que o cartório anexa. No e-SAJ, o `nome` sozinho
  ("Expedido Certidão") diz pouco. Como `MovimentoProcesso` só tem `Nome`, monte
  `"{nome} — {complemento}"` com o complemento truncado em 300 caracteres.
- No e-SAJ, `dataHora` só tem a data, com a hora em 00:00 no fuso de Brasília. No PJe vem data e
  hora.
- `avisos` não vazio significa **resultado parcial**: uma instância falhou e a outra respondeu.
  Trate como sucesso e registre os avisos em log, sem o conteúdo do processo.
- `partes` traz nomes e advogados. CPF e CNPJ **não** vêm, porque o juscraper os descarta.
  Cliente e responsável continuam **nunca** vindo da consulta.

### 3.2 `GET /v1/processos/{numero}/movimentacoes`: acompanhamento

| Query | Uso |
|---|---|
| `desde=yyyy-MM-dd` | Só andamentos nesta data ou depois. **O juscraper para de paginar o portal ao passar da data**, o que torna barato consultar centenas de processos por noite. Sem `desde`, vem o histórico completo |
| `instancias`, `sem_cache` | Como em 3.1. No job, use `sem_cache=false` (o padrão) |

Resposta `200`:

```json
{
  "numeroProcesso": "0700482-85.2026.8.02.0349",
  "tribunal": "TJAL",
  "encontrado": true,
  "completas": true,
  "movimentos": [
    {
      "id": "7688399a5b0f87f281013cbf7a92c58f",
      "nome": "Arquivado Definitivamente",
      "dataHora": "2026-09-22T00:00:00-03:00",
      "complemento": null,
      "possuiDocumento": false,
      "instancia": "G1"
    }
  ],
  "consultadoEm": "2026-11-25T17:08:00.23+00:00",
  "avisos": []
}
```

- `movimentos` vem consolidado (todas as instâncias), do mais recente para o mais antigo.
- **`id` é estável entre consultas**: é um SHA-256 de processo, instância, data/hora, nome e
  complemento, mais um ordinal para andamentos idênticos no mesmo dia. **Use-o como
  `ExternalId`**, com índice único `(tenant_id, processo_id, external_id)`. Andamento sem `id`
  deve ser ignorado.
- A estabilidade vale para listas completas e para consultas com `desde`. Com
  `max_movimentacoes`, o dia mais antigo pode vir cortado: **não use `max_movimentacoes` no
  acompanhamento**.
- `completas: false` significa que a lista foi truncada pelo teto de páginas do serviço.

### 3.3 `GET /v1/tribunais`: cobertura

```json
[{ "sigla": "TJSP", "sistema": "e-SAJ", "status": "validado", "portal": "https://esaj.tjsp.jus.br", "observacao": "…" }]
```

`status`:

- `validado`: confirmado com processo real;
- `experimental`: mesmo sistema de um validado, sem confirmação;
- `indisponivel`: captcha, firewall ou bloqueio geográfico.

Use esta lista para o front decidir o texto do botão, por exemplo "consultar no TJSP (portal)" ou
"consultar no DataJud". **Não** fixe a lista no código: ela muda por configuração do serviço.

Cobertura em nov/2026:

| Situação | Tribunais |
|---|---|
| Validados | TJSP, TJAL, TJDFT |
| Experimentais | TJCE, TJAC, TJMS, TJAM |
| Indisponíveis | TJMG, TJBA, TJPE, TJMA, TJRO, TJPB, TJAP, TJES, TJRN, TJSC, TJRS |
| Sem scraper | o resto, inclusive TRFs, TRTs e tribunais superiores. O juscraper responde `TRIBUNAL_NAO_SUPORTADO` e o **fallback DataJud cobre esses casos** |

## 4. Erros (ProblemDetails, RFC 9457)

`Content-Type: application/problem+json`:

```json
{
  "type": "https://github.com/MiguelFariasDev/juscraper/blob/main/docs/erros.md#captcha_exigido",
  "title": "CAPTCHA_EXIGIDO",
  "status": 503,
  "detail": "TJMG (PJe) não está disponível: a consulta pública do PJe exige reCAPTCHA.",
  "instance": "/v1/processos/50018258720268130439",
  "codigo": "CAPTCHA_EXIGIDO",
  "tribunal": "TJMG",
  "transitorio": false,
  "timestamp": "2026-11-25T14:47:55Z",
  "traceId": "00-…"
}
```

| `codigo` | HTTP | `transitorio` | Significado | Mapeamento no Iustitia |
|---|---|---|---|---|
| `NUMERO_CNJ_INVALIDO` | 400 | não | Número malformado ou com DV errado | `CNJ_INVALID_REQUEST` |
| `TRIBUNAL_NAO_SUPORTADO` | 422 | não | Tribunal sem scraper | `CNJ_TRIBUNAL_NOT_SUPPORTED` |
| `CAPTCHA_EXIGIDO` | 503 | não | Portal exige captcha (o juscraper não resolve captchas) | `CNJ_TRIBUNAL_NOT_SUPPORTED` |
| `ACESSO_BLOQUEADO` | 503 | não | Firewall/WAF ou bloqueio geográfico | `CNJ_TRIBUNAL_NOT_SUPPORTED` |
| `TRIBUNAL_INDISPONIVEL` | 502 | **sim** | Portal fora do ar após as tentativas | `CNJ_UNAVAILABLE` |
| `RESPOSTA_INESPERADA` | 502 | não | Portal mudou de layout | `CNJ_INVALID_RESPONSE` |
| `NAO_AUTORIZADO` | 401 | não | `X-Api-Key` ausente ou errada | `CNJ_AUTH_FAILED` |
| (sem ProblemDetails) timeout / rede | — | sim | Juscraper fora do ar | `CNJ_TIMEOUT` / `CNJ_UNAVAILABLE` |

Regras gerais:

- Mantenha o código original do juscraper disponível para a decisão de fallback (seção 5). Não
  decida só pelo `ErrorCode` do Iustitia, porque `CNJ_TRIBUNAL_NOT_SUPPORTED` junta três causas
  diferentes.
- **Nunca** logue o corpo das respostas de sucesso. Nos erros, logue `codigo`, `tribunal` e
  `status`.

## 5. Consulta do cadastro: juscraper principal, DataJud como fallback

### 5.1 Matriz de decisão

| Resultado do juscraper | Ação |
|---|---|
| `200` com `encontrado: true` | Usar o juscraper |
| `200` com `encontrado: false` | **Devolver "não encontrado", sem fallback.** O portal do tribunal é a fonte primária e mais atual que o DataJud, e um "não existe" do portal é confiável. Deixe uma opção `Juscraper:FallbackQuandoNaoEncontrado` (padrão `false`) para quem quiser tentar o DataJud mesmo assim |
| `TRIBUNAL_NAO_SUPORTADO`, `CAPTCHA_EXIGIDO`, `ACESSO_BLOQUEADO` | **Fallback para o DataJud.** É o caso principal: TRFs, TRTs, TJMG, TJRJ etc. |
| `TRIBUNAL_INDISPONIVEL`, `RESPOSTA_INESPERADA`, timeout, falha de rede, 5xx, circuito aberto | **Fallback para o DataJud** |
| `NAO_AUTORIZADO` (401) | Fallback para o DataJud **e** log de nível `Error`, porque é erro de configuração e precisa aparecer no alerta |
| `NUMERO_CNJ_INVALIDO` | **Sem fallback**: o número é inválido para as duas fontes. Devolver `CNJ_INVALID_REQUEST` |
| `Juscraper:BaseUrl` vazio | Só DataJud (comportamento atual) |

Se o DataJud **também** falhar, devolva o erro do DataJud, porque é a última fonte tentada, e
registre os dois códigos no log.

### 5.2 Implementação sugerida

Use **keyed services** do .NET 8 ou superior para registrar as duas implementações concretas e um
composto como implementação padrão da porta:

```csharp
services.AddKeyedScoped<IConsultaProcessoTribunal, JuscraperConsultaProcessoTribunal>(FonteConsulta.Juscraper);
services.AddKeyedScoped<IConsultaProcessoTribunal, DataJudConsultaProcessoTribunal>(FonteConsulta.DataJud);
services.AddScoped<IConsultaProcessoTribunal, ConsultaProcessoTribunalComFallback>();
```

Detalhes:

- O `JuscraperConsultaProcessoTribunal` usa `HttpClient` tipado (`AddHttpClient`) com
  `AddStandardResilienceHandler`: 60 s por tentativa, 1 retry, e o circuit breaker com
  `SamplingDuration` maior ou igual a 2 vezes o timeout da tentativa.
- Para o composto decidir o fallback sem depender de string de mensagem, o adaptador do
  juscraper precisa expor o código original. Duas formas:
  - `Error` com metadata, se o `Error` do BuildingBlocks permitir;
  - ou uma interface interna `IConsultaJuscraper` que devolve
    `Result<ProcessoTribunal?>` mais o `codigo`.
- Quando o circuito do juscraper estiver aberto, o composto vai **direto** ao DataJud, sem
  esperar timeout.

### 5.3 Telemetria

- Contador `consulta_tribunal.fonte` com as tags `fonte` (`juscraper` ou `datajud`), `tribunal`
  e `motivo_fallback` (o `codigo` do juscraper, ou `timeout`/`rede`).
- Nunca use o número do processo como tag ou atributo de métrica.

### 5.4 Contrato com a tela

- Acrescente `Fonte` (`"juscraper"` ou `"datajud"`) em `ConsultarProcessoTribunalResponse`, como
  campo opcional, sem quebrar o front.
- A tela pode mostrar "Dados do portal do TJSP" ou "Dados do DataJud/CNJ".
- O botão de consulta passa a ser habilitado para tribunais cobertos por **qualquer** das duas
  fontes, a partir de `GET /v1/tribunais` e da cobertura do DataJud.

### 5.5 Cuidado jurídico (registrar no ADR)

O ADR-064 restringe o DataJud a uso de **portfólio**: o Termo de Uso proíbe uso comercial
(cláusulas 3.3 e 3.8). Como fallback, o uso do DataJud **diminui**, mas não desaparece. Se o
Iustitia for comercializado, o fallback precisa ser desligado ou trocado por um provedor com
contrato. Deixe isso configurável com `Juscraper:FallbackDataJud` (padrão `true`).

## 6. Acompanhamento noturno de movimentações

(É o que o patch implementa; use como especificação se for fazer do zero.)

### 6.1 Fluxo

1. Job Hangfire `AcompanhamentoMovimentacoesJob`, cron `30 2 * * *`, **`Enabled: false` por
   padrão**, com `[DisableConcurrentExecution]` e `[AutomaticRetry(Attempts = 0)]`.
2. Para cada tenant (`ListTenantsComProcessosAtivosAsync`): `tenantContext.SetTenant(tenantId)`.
   Depois, para cada processo de `ListParaCapturaAsync` (ativos e suspensos), um por vez, com uma
   pausa de 1,5 s entre processos, enviar `SincronizarMovimentacoesCommand`.
3. Janela: `desde` = data do último andamento capturado **menos 3 dias**. Na primeira vez, sem
   `desde` (histórico completo).
4. Gravar só os `id` novos (`processo_movimentacoes`, índice único
   `(tenant_id, processo_id, external_id)`).
5. **Primeira sincronização = base**: grava sem alerta e sem aviso.
6. Quando há novidade:
   - alerta in-app ao responsável (`AlertType.Movimentacao`, `ReferenceId` = id do andamento
     mais recente). O texto traz só o número e a quantidade de andamentos;
   - **rascunho** `NotificacaoCliente` (`PendenteRevisao`), se o cliente tiver e-mail, estiver
     ativo e não estiver anonimizado. Há no máximo uma pendente por processo, e os andamentos
     das noites seguintes se somam a ela.
7. Falha de um processo **não relança**: conta e segue. Tribunal sem cobertura é contado à parte.

### 6.2 Aviso ao cliente: revisão humana obrigatória

- O job **nunca** envia ao cliente.
- O advogado revisa na fila:
  - `GET /api/v1/notificacoes-cliente?status=PendenteRevisao`;
  - `POST /api/v1/notificacoes-cliente/{id}/enviar` com `{ "mensagem": "texto editado" | null }`;
  - ou `POST /api/v1/notificacoes-cliente/{id}/descartar`.
- O envio é por `IEmailSender`, para o e-mail **atual** do cadastro. O HTML deve sair escapado.
  Se o provedor falhar, a notificação fica como `FalhaEnvio` e pode ser reenviada.
- No banco, o CHECK exige revisor e data de envio em toda notificação `Enviada`.

### 6.3 Endpoints de leitura

| Endpoint | Uso |
|---|---|
| `GET /api/v1/processos/{id}/movimentacoes?page&pageSize` | Timeline do processo |
| `POST /api/v1/processos/{id}/movimentacoes/sincronizar` | Sincronização sob demanda |

Os dois respeitam `ProcessoAcesso`: processo confidencial só para o responsável e para a
sociedade.

### 6.4 Fallback no acompanhamento (diferente da consulta)

Misturar fontes **no mesmo processo** cria duplicatas: o DataJud usa os nomes da Tabela
Processual Unificada e timestamps, enquanto o portal usa os nomes do cartório, às vezes só com a
data. Os mesmos andamentos teriam ids diferentes. Por isso:

| Situação | Ação |
|---|---|
| Juscraper com erro **transitório** (`TRIBUNAL_INDISPONIVEL`, timeout, rede) | **Sem fallback.** Pular o processo nesta noite; a próxima retenta |
| Juscraper sem cobertura **permanente** (`TRIBUNAL_NAO_SUPORTADO`, `CAPTCHA_EXIGIDO`, `ACESSO_BLOQUEADO`) | Acompanhar **pelo DataJud** (`movimentos[]` da API pública), com `Fonte = "datajud"` e `ExternalId = "datajud:" + SHA-256(codigo do movimento + dataHora ISO + nome)`, truncado em 64 caracteres |
| Processo que já tem andamentos de uma fonte | Continuar **na mesma fonte**. Só trocar de fonte explicitamente, gravando uma nova base sem notificar |

Implemente com um `IAcompanhamentoProcessual` composto, nos moldes do 5.2. Grave a fonte
escolhida por processo, derivada da `Fonte` dos andamentos já gravados.

### 6.5 Configuração

```json
"Juscraper": {
  "BaseUrl": "http://juscraper:8080/",
  "ConsultaTimeout": "00:01:00",
  "AcompanhamentoTimeout": "00:03:00",
  "FallbackDataJud": true,
  "FallbackQuandoNaoEncontrado": false
},
"Jobs": {
  "AcompanhamentoMovimentacoes": {
    "Enabled": false, "CronExpression": "30 2 * * *",
    "JanelaDias": 3, "PausaEntreProcessosMs": 1500, "TenantIds": []
  }
}
```

A `Juscraper:ApiKey` vai em user-secrets em dev e no Key Vault em produção. **Nunca** no
appsettings versionado.

No docker-compose de dev:

```yaml
juscraper:
  build: ../juscraper
  environment:
    Juscraper__Api__ApiKeys: ${JUSCRAPER_API_KEY}
  ports: ["8000:8080"]
```

## 7. Testes esperados

### Consulta com fallback (`ConsultaProcessoTribunalComFallback`)

- Juscraper encontra: devolve os dados do juscraper e **não** chama o DataJud.
- Juscraper responde `encontrado: false`: devolve não encontrado sem chamar o DataJud. Com
  `FallbackQuandoNaoEncontrado = true`, chama o DataJud.
- Para cada código de fallback (5.1) e para timeout: chama o DataJud e devolve o resultado dele.
- `NUMERO_CNJ_INVALIDO`: não chama o DataJud.
- As duas fontes falham: devolve o erro do DataJud.
- `FallbackDataJud = false`: devolve o erro do juscraper.
- `BaseUrl` vazio: só o DataJud.

### Adaptador do juscraper

- Mapeia cada `codigo` da seção 4.
- JSON ilegível vira `CNJ_INVALID_RESPONSE`.
- O nome do movimento sai como `"{nome} — {complemento}"`.

### Acompanhamento (o patch já cobre os quatro primeiros)

- A primeira sincronização não alerta nem notifica.
- Andamento novo gera alerta e rascunho.
- O rascunho pendente acumula os andamentos das noites seguintes.
- Cliente sem e-mail: só alerta.
- Erro transitório não usa fallback.
- Tribunal não suportado usa o DataJud com ExternalId prefixado.
- Processo com andamentos de uma fonte permanece nela.

### Convenções

Os testes de arquitetura e do catálogo de erros já existentes precisam passar:

- status HTTP só em {400, 401, 403, 404, 409, 500};
- `[Audited]` só com membros de `AuditAction`;
- classes em `Features` terminando com sufixos válidos (`Handler`, `Response`, `Mapping`…).

## 8. Critérios de aceite

- [ ] Cadastro de processo do TJSP, TJAL ou TJDFT preenchido pelo juscraper (`Fonte = juscraper`).
- [ ] Cadastro de processo do TRF1 ou do TJMG preenchido pelo DataJud via fallback, sem erro na tela.
- [ ] Juscraper desligado: o cadastro continua funcionando pelo DataJud.
- [ ] Job ligado em dev, com 1 processo: a primeira noite grava a base sem alerta; um andamento novo na noite seguinte gera alerta e rascunho.
- [ ] Rascunho enviado pelo advogado chega por e-mail; descartado não chega; nada chega sem revisão.
- [ ] Nenhum log contém conteúdo de andamento, nome de parte ou e-mail de cliente.
- [ ] ADR atualizado, `docs/integrations/juscraper.md` criado e catálogo de erros atualizado.
