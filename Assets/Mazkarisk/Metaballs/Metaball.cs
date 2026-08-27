using UnityEngine;

public class Metaball : MonoBehaviour {
	float colliderRadius = 0.01f; // 球の衝突判定の半径
	float physicalRadius = 0.02f; // 球の表示上・質量計算上の半径
	float triggerRadius = 0.04f;  // 球の影響範囲の半径

	// int maxJointsCount = 2; // 作成するJointの最大数

	public void setAttributes(float radius, float density) {
		// 各半径を計算
		colliderRadius = radius * 0.5f;
		physicalRadius = radius;
		triggerRadius = radius * 2f;

		// コライダーとトリガーの半径をそれぞれ設定
		transform.Find("Collider").GetComponent<SphereCollider>().radius = colliderRadius;
		// transform.Find("Trigger").GetComponent<SphereCollider>().radius = triggerRadius;

		// 体積を計算
		float volume = 4f / 3f * Mathf.PI * radius * radius * radius;

		// 体積と密度から質量を計算し設定
		GetComponent<Rigidbody>().mass = volume * density;
	}

	private void OnTriggerEnter(Collider other) {
		/*
		SpringJoint[] myJoints = GetComponents<SpringJoint>();
		if (myJoints.Length >= maxJointsCount) {
			return;
		}

		Rigidbody myRigidbody = GetComponent<Rigidbody>();
		Rigidbody otherRigidbody = other.attachedRigidbody;

		// 相手にRigidbodyが無いなら終了する。
		if (otherRigidbody == null) {
			return;
		}

		// 既にこちらからのJointが作成されているRigidbodyであれば終了する。
		for (int i = 0; i < myJoints.Length; i++) {
			if (myJoints[i].connectedBody == otherRigidbody) {
				return;
			}
		}

		// 既にあちらからのJointが作成されているのであれば終了する。
		SpringJoint[] otherJoints = otherRigidbody.transform.GetComponents<SpringJoint>();
		for (int i = 0; i < otherJoints.Length; i++) {
			if (otherJoints[i].connectedBody == myRigidbody) {
				return;
			}
		}

		var newJoint = gameObject.AddComponent<SpringJoint>();
		newJoint.autoConfigureConnectedAnchor = false;
		newJoint.anchor = Vector3.zero;
		newJoint.connectedAnchor = otherRigidbody.transform.InverseTransformPoint(other.ClosestPoint(myRigidbody.position));
		newJoint.connectedBody = otherRigidbody;
		newJoint.minDistance = triggerRadius;
		newJoint.maxDistance = triggerRadius;
		newJoint.breakForce = 0.1f;
		*/
	}

	private void OnDrawGizmosSelected() {
		Gizmos.color = Color.blue;
		Gizmos.DrawWireSphere(Vector3.zero, physicalRadius);
	}
}
