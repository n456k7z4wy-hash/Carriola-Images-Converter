# Carriola Images Converter — versão 1.0

> Código-fonte do aplicativo para Windows e scripts de geração do instalador. O Setup.exe compilado ainda não foi publicado neste repositório.


Aplicativo desktop para Windows, desenvolvido em C# com .NET 8, WinUI 3 e Magick.NET, para converter imagens individualmente ou em lote. O projeto não utiliza Python.

## Abrir no Visual Studio

1. Baixe ou clone este repositório.
2. No Visual Studio com as ferramentas de desenvolvimento WinUI instaladas, escolha **Abrir um projeto ou uma solução** e abra **CarriolaConverter.csproj**. Não é necessário um arquivo de solução para abrir este projeto.
3. Aguarde a restauração dos pacotes NuGet e selecione a plataforma **x64**. A biblioteca de conversão incluída no projeto é Magick.NET-Q16-x64.
4. Compile e execute no Windows. Para usar o modo empacotado MSIX em outra máquina, selecione ou crie um certificado de desenvolvimento no Visual Studio. O certificado pessoal usado pelo autor não acompanha o repositório.

## Gerar o instalador

Com o Visual Studio, as ferramentas WinUI e o Inno Setup instalados no Windows, execute **Distribuicao/Gerar-Instalador.cmd**. O gerador verifica o compilador do Inno Setup, publica em Release x64, obtém o componente Visual C++ oficial e monta o Setup.exe. A primeira geração precisa de acesso à internet.

O resultado fica em uma pasta **Carriola-Distribuicao**, ao lado da pasta do projeto, dentro de uma subpasta com data e hora. Para alterar a versão do próximo instalador, edite **Distribuicao/Versao.txt**.

O instalador tradicional não usa a chave privada de teste MSIX. Arquivos de certificado, configurações pessoais do Visual Studio, saídas de compilação e instaladores gerados estão excluídos pelo arquivo .gitignore.

## Conversão e controle da fila

- Processamento assíncrono com concorrência limitada para manter a interface utilizável durante os lotes.
- Entrada de imagens por seleção de arquivos, pastas, arrastar e soltar e área de transferência com Ctrl+V.
- Busca opcional em subpastas e prevenção de entradas repetidas.
- Progresso por arquivo, progresso total, filtros de estado e remoção de itens da fila.
- Cancelamento do lote e tratamento individual de erros, permitindo continuar o processamento dos demais arquivos.
- Renomeação automática e escolha de nomes disponíveis, preservando os arquivos existentes na pasta de destino.

## Qualidade, formatos e tamanho

- Formatos de saída previstos: JPG, PNG, WEBP, AVIF, TIFF, BMP e HEIC. A interface mostra os formatos cujo encoder passa pela verificação de escrita na instalação em uso.
- Ajuste de qualidade nos formatos que oferecem compressão configurável.
- Redimensionamento por maior lado, largura, altura, porcentagem ou limites de largura e altura, preservando as proporções.
- Opção para permitir ampliação de imagens menores.
- Tratamento de transparência e escolha da cor de fundo para formatos sem canal alfa.
- Miniaturas e comparação antes/depois com zoom e rolagem sincronizados.
- Exibição dos tamanhos de entrada e saída e da economia ou do aumento real de tamanho.
- Correção do erro do encoder AOM ao gerar AVIF com qualidade 100, aplicada tanto à conversão quanto à prévia.

## Interface e personalização

- Interface escura com WinUI 3, efeito Mica e animações na área de arrastar e soltar.
- Identidade visual com o nome **Carriola Images Converter**, logo, ícone e abertura animada.
- Preferências salvas e perfis reutilizáveis de conversão, incluindo a pasta de destino.
- Progresso integrado à barra de tarefas do Windows.
- Notificação de conclusão e opção de abrir automaticamente a pasta de saída.

## Memória e estabilidade

- Limites compartilhados de recursos do ImageMagick e controle adicional de concorrência para imagens grandes.
- Miniaturas carregadas por demanda, cache limitado e lista virtualizada.
- Prévia com resolução de exibição limitada para reduzir o consumo de memória; a gravação final segue o tamanho escolhido.
- Gravação da saída em arquivo temporário antes de movê-la para um nome disponível.
- Limpeza dos arquivos temporários da sessão e liberação dos recursos de conversão.
- Publicação configurada com trimming desativado para preservar o funcionamento dos recursos utilizados pela interface.

## Instalador para distribuição

- Instalador tradicional **Setup.exe** gerado com Inno Setup, em português e com tema escuro.
- Publicação x64 com componentes do .NET e do Windows App SDK incluídos.
- Inclusão do redistribuível Visual C++ oficial, com verificação de assinatura durante a geração.
- Atalho no menu Iniciar, atalho opcional na área de trabalho e desinstalação pelas Configurações do Windows.
- Ajustes de registro das notificações para a aplicação instalada fora de MSIX.
- Geração pelo arquivo `Distribuicao/Gerar-Instalador.cmd`, com logs e arquivo SHA-256 do resultado.
- Correção da identificação do Inno Setup: a versão e o tema são verificados pelo próprio compilador antes de publicar o aplicativo.
- Numeração de versão centralizada em `Distribuicao/Versao.txt`, identificação estável para atualizações e proteção contra instalação de uma versão anterior sobre uma mais recente.

## Validação relatada pelo autor

- Compilação e execução do aplicativo no Windows.
- Conversão de uma imagem e de um lote com mais de 50 imagens.
- Abertura automática da pasta ao concluir o lote.
- Conversão para AVIF após a correção do encoder.
- Funcionamento da logo e da abertura animada.
- Geração do instalador e teste de instalação e uso em outro computador, com funcionamento confirmado.

Esses resultados correspondem aos testes manuais relatados pelo autor. Não representam certificação de compatibilidade com todas as versões do Windows, todos os formatos ou todas as configurações de hardware.

## Comportamentos atuais

- Imagens animadas e arquivos com várias páginas são processados somente no primeiro quadro/página.
- RAW é formato de entrada quando o leitor correspondente está disponível; não é uma opção de exportação.
- A prévia é limitada a 2048 pixels no maior lado e não substitui uma inspeção em resolução nativa de imagens maiores.
- As preferências e os perfis são restaurados ao reabrir; a fila de arquivos não é restaurada.
- As atualizações do aplicativo são instaladas executando uma nova versão do Setup.exe; esta versão não possui atualização automática.

