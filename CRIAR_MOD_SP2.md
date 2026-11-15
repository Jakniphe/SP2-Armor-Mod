# Como o Claude cria um mod de SimplePlanes 2 (a partir do zero ou "um mod aleatório")

Guia para o Claude. Foi escrito a partir do que este projeto (o mod de blindagem, pasta `Armor Mod/`) já fez e verificou no jogo. Tudo que está aqui foi usado de verdade neste repositório, exceto onde estiver marcado como **não verificado**. Quando um fato não estiver neste guia, leia o código decompilado do jogo em vez de supor.

## 0. Regras de conduta (valem sempre)

- **Quem pede:** normalmente um jogador que conhece bem o SP2 e é iniciante em programação e em Unity. Explique em passos pequenos, diga o que foi testado e o que é rascunho, responda no idioma da última mensagem.
- **O Claude não roda o jogo nem compila para ele.** Todo código é "não testado" até o usuário compilar no Visual Studio e mandar `BepInEx/LogOutput.log` ou prints. Diga isso ao entregar. Não afirme que algo funciona.
- **Antes de entregar:** rode a verificação de compilação (seção 9) e releia o código procurando o que ela não pega. Um erro de build custa uma volta inteira ao usuário.
- **Git:** desenvolva na branch combinada, só atualize o `master` quando pedirem, não abra PR sem pedido, não suba a versão antes de terminar o conjunto. Termine commits com os trailers que a sessão exigir.
- **Privacidade:** não inclua dados pessoais (nome, e-mail) em nada que vá para fora.
- **Respeito a terceiros:** código de outro modder só entra com autorização dele; dê o crédito no `claude.md` e no changelog.

## 1. "Faça um mod aleatório": como escolher e conduzir

Se o usuário só disser "crie um mod" (ou "aleatório"), **você escolhe a ideia**, mas dentro destas regras:

1. **Uma ideia pequena e verificável**, que se prove com uma linha de log ou algo visível em voo. Nada que dependa de adivinhar a API do jogo sem ler o código.
2. **Use pelo menos uma feature nativa do jogo** (seção 4) além do patch em si: painel próprio, seção no designer, variável do Funky Trees, tecla, configuração, sincronização no multiplayer, câmera, etc. Um mod "completo" combina 3 ou 4 delas.
3. **Anuncie a ideia em 3 linhas** (o que faz, onde atua, como o jogador vê) e siga sem esperar resposta, a menos que haja uma decisão que seja do usuário.
4. **Entregue em fases**, cada uma com uma verificação (um log esperado, um comportamento). Fase 1 sempre: o plugin carrega e escreve uma linha no log.

Sementes de ideia (escolha uma, misture ou invente outra):

| Ideia | Features que usa |
|---|---|
| Contador de tiros e precisão por canhão, mostrado num painel | patch em canhão, painel Juicy, configuração |
| Peça "radiador" que superaquece com o motor e escreve a temperatura numa variável | tag no nome, designer, Funky Trees, `Update` |
| Marcador de alvo (linha e distância até o veículo mirado) | raycast, `LineRenderer`, tecla, câmera |
| Combustível que vaza com o dano do tanque e sincroniza no multiplayer | dano da peça, variável, mensagem de rede |
| "Caixa-preta": grava a trajetória do voo e mostra como linha depois | `Update`, desenho de linha, painel, tecla |
| Ejetar peça marcada ao receber dano acima de X | tag, `OnDamaged`, designer, undo |
| Paraquedas de freio acionado por tecla, com sliders no designer | tag, designer, tecla, física |

## 2. Estrutura do projeto

- Plugin **BepInEx 5.4.23.5 x64 (Mono)**, C# **netstandard2.1** (o `Game.dll` é construído para isso). Visual Studio Community, sem o Unity Editor. O jogo é Unity 6.
- `ArmorFragments.csproj` (SDK-style: todo `.cs` novo entra sozinho) referencia, a partir da pasta do jogo (`GameDir`; a pasta de dados pode se chamar `SimplePlanes 2_Data` ou `SimplePlanes 2 (1.1)_Data`, o `.csproj` já acha): `BepInEx.dll`, `0Harmony.dll`, `Game.dll`, `UnityEngine*.dll` (Core, Physics, UI...), `Jundroo.Packages.dll` (Juicy, a UI), `Jundroo.Common.dll`, `Unity.InputSystem`, `Unity.TextMeshPro`, URP. Todas com `<Private>False</Private>` (não copiar para a saída).
- Classe principal: `[BepInPlugin(guid, nome, versão)]` herdando `BaseUnityPlugin`. `Awake()` faz: `Log = Logger`, `Config.Bind(...)` de tudo, registro de patches, linha de log com a versão. Uma `const string Version`, mostrada no log e no painel (assim se distingue DLL velha de nova).
- Saída: o `.dll` vai para `<jogo>\BepInEx\plugins\`. Log em `<jogo>\BepInEx\LogOutput.log`. Configuração em `BepInEx\config\<guid>.cfg`.
- Material de estudo (só pessoal, não vai para o repositório): `Game.dll` decompilado (classes em `Assets.Scripts.*`), `Jundroo.Packages.dll`, e mods de referência (GravyMenu e os do ecossistema BepInEx para SP2).

## 3. Como atuar no jogo: Harmony

- **Patch simples:** `[HarmonyPatch(typeof(Classe), "Metodo")]` numa classe estática com `Prefix`/`Postfix`. Parâmetros especiais: `__instance`, `__result` (use `ref`), `__state`, `___campoPrivado` (três sublinhados + nome do campo), argumentos pelo nome real, ou por posição `__0..__n` quando o nome não existe (métodos de RPC gerados: o nome `P_0` não é encontrado; use posição).
- **Métodos privados, sobrecargas e overrides:** passe o nome como string; para um conjunto de overrides use `TargetMethods()`.
- **Um patch que falha não pode derrubar os outros:** `SafePatcher.PatchAll` aplica classe por classe e registra no log "Patch X skipped".
- **Prefix retornando `false`** pula o método original (foi assim que o mod entregou o próprio XML de UI).
- **Depois de uma atualização do jogo:** o mod pode quebrar sem aviso (já aconteceu: `RendererMaterialMap.Renderer` mudou de `MeshRenderer` para `Renderer`). Mantenha um `CompatibilityCheck` que, ao carregar, confere por reflexão cada método/campo usado e escreve no log `[COMPAT]` o que mudou. Para tipos que mudam de forma, use reflexão em vez de acesso direto.
- **Ambiguidade de nomes:** `Assembly` conflita com outro tipo do jogo; escreva `System.Reflection.Assembly`.
- **Estado entre frames:** o plugin tem `Update()`; chame dali o que precisa rodar todo frame (`ModuleOutputs.Tick()`, `ImpactCamera.Tick()`...). Reconstrução de UI nunca dentro de um clique (destrói o controle clicado): marque `rebuildPending` e refaça no `Tick`.

## 4. Catálogo das features do jogo que dá para usar

Cada item diz o que existe, como se conecta e a armadilha conhecida.

### 4.1 Peças, modificadores e dados
- `PartScript` (`Assets.Scripts.Craft.Parts`) é a peça em voo; `PartScript.Part` é o `PartData` (`Name`, `Id`, `Modifiers`, `LoadedMass`, `PartType`). Os modificadores são `PartModifierData` (`CannonData`, `JFuselageData`, `JEngineData`, `FuelTankData`, `PropellerAssemblyData`, `TurboshaftData`, `JetEngineData`...). Teste o tipo com `modifier is T`.
- Massas: o jogo guarda toda massa em **1/100** do valor real (o designer divide por 0,01 para mostrar kg). Divida por 0,01 antes de usar em física.
- Aeronave: `AircraftScript` (`Parts`, `MainCockpit`, `Velocity`, `RemoteAircraft`, `VariableSystem`, `NetworkAircraft`, `GetPartById`). Jogador local: `FlightSceneScript.Instance.LocalPlayer.Aircraft`.

### 4.2 Guardar dados numa peça sem alterar o jogo: tags no nome
- O nome editável da peça (`Part.Name`) é salvo com a aeronave, entra no desfazer e viaja no multiplayer. O mod guarda propriedades nele, entre colchetes, depois do rótulo do jogador: `Placa [ARM t=20 h=100] [MOD v=Motor]`. Um `TagCodec` lê (`Parse`, com cache, só leitura; `ParseCopy` para editar) e escreve (`Compose`) tudo; chaves desconhecidas e antigas são ignoradas, para o formato poder evoluir.
- Esta é a forma mais barata de dar **dados por peça** sem mexer em arquivos de save.

### 4.3 Designer (construtor)
- Seção própria dentro do painel de propriedades da peça: patches em `PartPropertiesPanelScript` (`InitializeDesignerPanel` cria, `SetSelectedPart` seleciona, `UpdateCommonPartProperties` avisa que o nome mudou). Os controles vêm de modelos do próprio jogo (`PanelBuilder`: `control-header`, `control-toggle`, `control-slider`, `control-text`).
- Cada alteração reescreve o nome da peça (`part.Part.Name = ...`, `modifier.OnPartNameChanged(name)`), atualiza o campo de nome do jogo e cria um passo de desfazer ao soltar o slider (`Designer.Instance.CreateUndoStepForSelectedPart`). Durante o arraste, atualize o nome sem criar passo de desfazer.
- Mostre só as linhas que fazem sentido para o modificador da peça selecionada. O jogo não avisa quando um controle dele muda (ex.: o tipo de projétil do canhão): consulte o valor algumas vezes por segundo e refaça as linhas se mudou.

### 4.4 Painel próprio no voo (UI Juicy)
- A UI do jogo é XML + scripts achados pelo nome. Três patches entregam o seu: botão no menu de voo (`MenuPanelScript.OnWidgetInitialized`), o XML (`Jundroo.Juicy.ResourceLoader.LoadXml`, prefix devolvendo o seu layout) e o tipo do script (`ResourceLoader.GetScriptType`, postfix que procura no seu assembly os nomes com o seu namespace).
- A janela herda `PanelDialogScript` (`IsModal => false` para continuar voando). Linhas "vivas" (`Live(rótulo, () => texto)`) são atualizadas a cada 0,5 s; sliders e toggles ligados a `ConfigEntry` regravam a configuração.

### 4.5 Funky Trees e variáveis
- `VariableSystemScript.AddVariable(nome)` cria uma variável da aeronave e `AircraftVariable.SetValue(valor, prioridade)` escreve nela (pode chamar do `Update`; o jogo enfileira para o próximo `StartFrame`). O jogo apaga variáveis que nada usa a cada `RefreshVariables`, então recrie num postfix. Chame `RefreshVariables` no designer depois de mudar tags para o nome aparecer nas listas do Funky Trees.
- Variáveis de saída do mod: nível de dano 0..1 por módulo; `<grupo>_rounds`, `<grupo>_fire`, `<grupo>_boom` nos porta-munições.

### 4.6 Dano, explosões e destruição
- `PartScript.OnDamaged(int? atacante, float dano, Vector3 ponto, Vector3 direção)` soma em `PartDamage`; vida = `MaxHealth`; destruída = corpo todo desligado (`BodyScript.DisconnectParts` → `SetActive(false)`). Em aeronave remota `OnDamaged` retorna cedo: o dano a outro jogador vai por `Aircraft.NetworkAircraft.DamagePart(...)` (RPC ao dono).
- `FlightSceneScript.CreateExplosion(...)` cria explosões do jogo. Canhão: `CannonData` tem `ImpactDamageScalar`, `ExplosionScalar` (setters privados, por reflexão), `AmmoType`, `ProjectileVolume`, `ProjectileVelocity`, `CaliberInMilimeters`.
- `CannonScript.Fire` instancia o projétil (`Rigidbody`); `CannonProjectileScript.OnCollisionEnter` trata o impacto (campos privados `_previousFrameVelocity`, `_cannonScript`, `_isExplosive`). Use a velocidade do frame anterior, não a atual.

### 4.7 Física, raycasts e colisores
- `Physics.RaycastNonAlloc` com buffer fixo; ignore projéteis e use a máscara sem a camada da UI de mundo. Para saber quanto um raio anda dentro de uma peça: `Collider.Raycast` para frente (entrada) e para trás a partir de um ponto além (saída).
- Camadas: a câmera principal exclui 3, 14, 15, 22, 24, 27, 31; as camadas 28 a 30 são "Mod Layer 1-3" (use 29 para o seu desenho extra e esconda-a da câmera principal).

### 4.8 Desenho e efeitos
- `LineRenderer` e esferas primitivas funcionam, com um material do shader que existir na build (o jogo é URP e tira shaders: tente `Sprites/Default`, `Hidden/Internal-Colored`, `Unlit/Color`, `Universal Render Pipeline/Unlit`, `UI/Default` e registre qual funcionou). Objetos `MonoBehaviour` criados por você (animações) vivem no mundo; destrua-os no fim.
- "Raio-X": cópias translúcidas dos meshes (`MeshRenderer`/`MeshFilter`; peças com malha de pele não aparecem), escondendo os originais com `Renderer.forceRenderingOff`.

### 4.9 Câmeras
- `CameraManagerScript.Instance.MainCamera`, `.Controller` (ex.: `OrbitCameraController`). Uma câmera extra renderizada para uma janela pequena (picture-in-picture, como a câmera de impacto) precisa de máscara de camadas, luz e URP configurados; escreva um diagnóstico de log (`[CAMDIAG]`) antes de depurar às cegas.

### 4.10 Entrada
- Teclas pelo Input System, configuráveis (`ConfigEntry`), com um botão equivalente no painel. Evite `[`, `]`, `P`, `G`, `L` e Alt (o jogo já usa ou dá problema). F8 está em uso pelo raio-X do veículo.

### 4.11 Configuração
- `Config.Bind(seção, chave, padrão, descrição/`AcceptableValueRange`)`. A faixa aceita é lida pelo painel para montar o slider. Valor que decide dano em outros jogadores deve ser **global** (seção 4.12); o resto fica local (visual, teclas, logs).

### 4.12 Multiplayer (FishNet)
- O projétil de canhão só existe no jogo de quem atira; os outros veem só o clarão do cano. Quem atira detecta a colisão e manda o dano ao servidor, que repassa ao dono da peça.
- Canal de mensagens de peça do próprio jogo: `INetworkAircraft.SendPartNetworkMessage`; interceptar `ProcessPartNetworkMessage`; o jogo ignora tipos desconhecidos (este mod usa 200 = configuração do host e 201 = "tenho o mod"). O host repete as configurações globais a cada 5 s; o cliente aplica e restaura ao sair, e nunca grava os valores do host no seu `.cfg`.
- Se só o host tem o mod, ele pode recalcular o que o atirador sem o mod fez (resolvedor do host). Modo pacífico: o servidor descarta o dano entre jogadores (avise no log).
- **Não verificado em cada versão:** teste multiplayer com outros jogadores sempre que mexer em dano ou rede.

## 5. Padrões que valem em qualquer mod

1. **Tudo configurável e com padrão seguro.** Um slider no painel para cada número de ajuste, com descrição.
2. **Logs úteis e curtos**, com prefixos (`[COMPAT]`, `[HOST]`, `[SIM]`): o log é como o usuário reporta bugs. Registre as decisões, não só os erros.
3. **Função de teste dentro do jogo**, como o "tiro simulado" do mod de blindagem: um botão que dispara a lógica sem precisar de adversário, mostrando o resultado.
4. **Falhe com graça:** `try/catch` ao redor de tudo que o jogo pode mudar; o mod nunca deve travar o voo.
5. **Cache por frame/tiro** para o que é caro (colisores, densidades, tags).
6. **Documente no `claude.md`** o que foi feito, o que não foi testado e as decisões. O próximo Claude começa por ele.
7. **Documento de contexto por mod/feature grande** (como `AMMO_RACKS_CONTEXT.md`).

## 6. Fluxo de trabalho por fase

1. **Escolha/pergunta mínima.** Só pergunte o que é do usuário decidir (um número de balanço, um comportamento ambíguo). O resto, escolha um padrão e diga qual.
2. **Leia o código do jogo** antes de assumir (a classe, os campos, quem chama). Procure com `grep` no decompilado.
3. **Escreva a menor fase que prova o caminho** (log). Depois a lógica. Depois a interface. Depois a rede.
4. **Rode a verificação de compilação**, releia, entregue com: o que mudou, o que esperar no log, como testar, o que ainda não foi testado.
5. **Receba o log do usuário**, corrija, repita. Só suba a versão quando o conjunto estiver pronto.

## 7. Erros que já aconteceram (não repita)

- Slider "ainda visível" e tecla morta: era a **DLL antiga** na pasta de plugins. A linha de versão no log/painel resolve na hora.
- Fantasmas do raio-X invisíveis: estavam na camada 3 (excluída da câmera); use uma camada configurável.
- Visão do raio-x demorando: não espere ociosidade; construa rápido, com limite de tempo e sem lançar exceção.
- Peça destruída some do raio-x: o jogo desliga o **corpo inteiro**; congele os fantasmas **antes** de `DisconnectParts` (patch prefix) em um suporte que siga o cockpit.
- Bug de "nome de parâmetro" em RPC gerado: use posição `__0..__n`.
- Atualização do jogo trocou um tipo: reflexão e `CompatibilityCheck`.
- Script de edição que falha pela metade: aplique edições em um arquivo de script com asserções (a contagem de ocorrências de cada trecho deve ser 1) e confira o resultado.

## 8. Entrega e versionamento

- Um commit por conjunto coerente, mensagem em inglês que diga o que e por quê.
- `README`/changelog no estilo curto de Discord, em inglês, uma linha por mudança (agrupadas: novidades, correções), com o link do release e a marcação do cargo de testadores.
- Versão: patch para correções, minor para features. Atualize a constante `Version`, o `claude.md` e só então o `master`.

## 9. Verificação de compilação sem as DLLs do jogo

O Claude não tem as `.dll` do jogo, mas tem o código decompilado dele. Dá para compilar o mod contra o decompilado: um programa Roslyn pequeno (`tools/compile-check` deste repositório) junta o mod e o decompilado numa compilação e mostra só os erros dos arquivos do mod (o decompilado tem erros próprios, que são ignorados). Pega tipo errado, membro inexistente, argumentos errados, variável não atribuída, conflito de escopo. Detalhes e instalação: `tools/compile-check/README.md`. Em contêiner novo: `apt-get update && apt-get install -y dotnet-sdk-8.0`, depois `dotnet restore && dotnet build` na pasta da ferramenta e `./fetch-libs.sh`.

O que ela não prova: comportamento, strings (alvos do Harmony, nomes de reflexão) e se o jogo instalado é a mesma versão do decompilado. Pacotes que o decompilado não tem (Input System, UI do Unity, FishNet) entram como stubs. Sem o decompilado ou sem `dotnet`, volte para a checagem de sintaxe (`tree-sitter-c-sharp`) e a releitura atenta (declarações duplicadas em escopos aninhados, `out`/`ref`, variáveis não atribuídas).

## 10. Checklist antes de dizer "pronto"

- [ ] A ideia está descrita em 3 linhas e usa features do jogo (seção 4).
- [ ] Plugin carrega e loga a versão; patches aplicados aparecem no log.
- [ ] Toda constante de balanço está numa configuração, com padrão.
- [ ] Painel e/ou designer mostram o que o jogador precisa para testar.
- [ ] Compilação verificada contra o decompilado; releitura feita.
- [ ] Efeitos em multiplayer pensados (global × local; dano em outros jogadores).
- [ ] `claude.md` atualizado com o que não foi testado.
- [ ] Mensagem final diz o que testar e que log mandar de volta.
