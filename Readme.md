# レイトレーシング・ガラスエフェクトシェーダー

Unity 6.4 (URP) 環境向けのガラスエフェクトシェーダーです。ハードウェアレイトレーシングを利用して屈折と反射を計算し、物理的に正確な描画を実現しています。
パフォーマンスを考慮し、スクリーンスペースと正確なレイトレーシングを組み合わせたハイブリッドアプローチを採用しました。

## 論文

処理の説明についての論文が作成中ですが、今までの物ご覧いただけます：

* [ハイブリッドレイトレについて](hybrid.md)
* [スクリーンスペースレイトレについて](screenSpace.md)
* [色収差](chromaticAberration.md)

## Examples
![Diamond](Examples/URP-Diamond.gif)
![Fresnel Lens](Examples/URP-Fresnel.gif)
![Dove Prism](Examples/URP-Dove.gif)
